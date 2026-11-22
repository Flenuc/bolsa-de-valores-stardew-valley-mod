using System.Globalization;
using ZuzuMarket.Core.Ledger;
using ZuzuMarket.Core.Money;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Markets;

/// <summary>Opciones europeas sobre acciones: listado, prima, compra y lanzamiento, ejercicio al vencimiento.</summary>
public sealed partial class Broker
{
    public OptionSeries? FindOption(string id) =>
        _s.Derivatives.Options.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<OptionSeries> OptionsFor(string ticker, FiscalPeriod period) =>
        _s.Derivatives.Options.Where(o => o.Ticker == ticker && o.Period == period).OrderBy(o => o.Strike).ThenBy(o => o.Type).ToList();

    public IReadOnlyList<FiscalPeriod> OptionPeriods() =>
        _s.Derivatives.Options.Select(o => o.Period).Distinct().OrderBy(p => p.FirstDay).ToList();

    public OptionPosition? OptionPositionFor(string seriesId) =>
        _s.Derivatives.OptionPositions.FirstOrDefault(p => p.SeriesId == seriesId);

    /// <summary>Volatilidad anual de una acción (la diaria de la empresa escalada al año de 112 días).</summary>
    public double AnnualVolatility(string ticker) =>
        (_stocks.Find(ticker)?.Volatility ?? 0.02) * Math.Sqrt(EconomicDate.DaysPerYear) * (double)_difficulty().PriceVolatility;

    /// <summary>Tasa continua anual equivalente a la tasa de política mensual.</summary>
    public double AnnualRate => Math.Log(1 + ReferenceRate) * EconomicDate.SeasonsPerYear;

    /// <summary>Prima teórica por acción (Black-Scholes).</summary>
    public double OptionPremium(OptionSeries series, EconomicDate date, int timeOfDay)
    {
        double spot = _stocks.PriceAt(series.Ticker, timeOfDay);
        double years = Math.Max(0, date.DaysUntil(series.Expiry)) / (double)EconomicDate.DaysPerYear;
        return BlackScholes.Price(series.Type, spot, series.Strike, years, AnnualRate, AnnualVolatility(series.Ticker));
    }

    public double OptionDelta(OptionSeries series, EconomicDate date, int timeOfDay)
    {
        double spot = _stocks.PriceAt(series.Ticker, timeOfDay);
        double years = Math.Max(0, date.DaysUntil(series.Expiry)) / (double)EconomicDate.DaysPerYear;
        return BlackScholes.Delta(series.Type, spot, series.Strike, years, AnnualRate, AnnualVolatility(series.Ticker));
    }

    private void ListOptions(EconomicDate date, int timeOfDay)
    {
        for (int k = 0; k < DerivativesRules.OptionMaturities; k++)
        {
            FiscalPeriod period = date.FiscalPeriod.AddSeasons(k);
            EconomicDate expiry = ExpiryOf(period);
            if (expiry < date || _s.Derivatives.Options.Any(o => o.Period == period))
            {
                continue;
            }

            foreach (CompanyDefinition company in _stocks.Companies)
            {
                double spot = _stocks.PriceAt(company.Ticker, timeOfDay);
                foreach (double strike in DerivativesRules.OptionStrikes.Select(m => RoundStrike(spot * m)).Distinct())
                {
                    foreach (OptionType type in new[] { OptionType.Call, OptionType.Put })
                    {
                        string id = string.Create(CultureInfo.InvariantCulture,
                            $"{company.Ticker}-{(type == OptionType.Call ? 'C' : 'P')}{strike:0.#}-{SeasonLetter(period.Season)}{period.Year}");
                        if (FindOption(id) is null)
                        {
                            _s.Derivatives.Options.Add(new OptionSeries { Id = id, Ticker = company.Ticker, Type = type, Strike = strike, Period = period, Expiry = expiry });
                        }
                    }
                }
            }
        }
    }

    /// <summary>Ejercicio redondeado a valores "lindos": de a 0,5 por debajo de 20, de a 1 hasta 100 y de a 5 por encima.</summary>
    public static double RoundStrike(double value)
    {
        double step = value < 20 ? 0.5 : value < 100 ? 1 : 5;
        return Math.Max(step, Math.Round(value / step) * step);
    }

    // ───────────────────────────── Operatoria ─────────────────────────────

    public DerivativeQuote QuoteOption(string id, TradeSide side, long quantity, EconomicDate date, int timeOfDay)
    {
        OptionSeries? series = FindOption(id);
        if (series is null || series.Expiry < date)
        {
            return new DerivativeQuote(InstrumentKind.Option, id, side, quantity, 0, 0, 0, 0, 0, TradeError.UnknownInstrument);
        }

        double mid = OptionPremium(series, date, timeOfDay);
        double unit = Math.Max(0.01, mid * (1 + ((side == TradeSide.Buy ? 1 : -1) * DerivativesRules.OptionSpread / 2)));
        int lot = DerivativesRules.OptionLot;
        decimal notional = MoneyRounding.Round((decimal)(unit * lot * Math.Max(0, quantity)));
        decimal commission = notional > 0 ? MoneyRounding.Round(Math.Max(notional * DerivativesRules.OptionCommissionRate, Rules.MinimumCommission)) : 0;
        decimal iva = MoneyRounding.Round(commission * Rules.CommissionIvaRate);
        long current = OptionPositionFor(series.Id)?.Quantity ?? 0;
        long after = current + (side == TradeSide.Buy ? quantity : -quantity);
        decimal marginAfter = RequiredMargin(date, timeOfDay, series.Id, after);

        decimal cashNeeded = commission + iva + Math.Max(0, marginAfter - MarginBalance) + (side == TradeSide.Buy ? notional : -notional);
        TradeError error = ValidateDerivative(quantity, date, timeOfDay, cashNeeded, Math.Abs(after) > Math.Abs(current));
        return new DerivativeQuote(InstrumentKind.Option, series.Id, side, quantity, unit, notional, commission, iva, marginAfter, error);
    }

    public DerivativeQuote ExecuteOption(string id, TradeSide side, long quantity, EconomicDate date, int timeOfDay)
    {
        DerivativeQuote q = QuoteOption(id, side, quantity, date, timeOfDay);
        if (q.IsValid)
        {
            ApplyOptionTrade(q, date, timeOfDay);
        }

        return q;
    }

    private void ApplyOptionTrade(DerivativeQuote q, EconomicDate date, int timeOfDay)
    {
        OptionPosition? p = OptionPositionFor(q.Instrument);
        if (p is null)
        {
            p = new OptionPosition { SeriesId = q.Instrument };
            _s.Derivatives.OptionPositions.Add(p);
        }

        decimal perContract = (decimal)q.UnitPrice * DerivativesRules.OptionLot;
        long remaining = q.Quantity;

        // Primero se cierra lo que haya del lado contrario.
        if (q.Side == TradeSide.Buy && p.Quantity < 0)
        {
            long close = Math.Min(remaining, -p.Quantity);
            CloseOptionPart(p, close, MoneyRounding.Round(perContract * close), date, buyingBack: true);
            remaining -= close;
        }
        else if (q.Side == TradeSide.Sell && p.Quantity > 0)
        {
            long close = Math.Min(remaining, p.Quantity);
            CloseOptionPart(p, close, MoneyRounding.Round(perContract * close), date, buyingBack: false);
            remaining -= close;
        }

        if (remaining > 0)
        {
            decimal amount = MoneyRounding.Round(perContract * remaining);
            if (q.Side == TradeSide.Buy)
            {
                Post(date, $"Compra de {remaining} {q.Instrument}", "entry.option-buy", q.Instrument,
                    JournalLine.ToDebit(SystemAccounts.PrimasCompradas, amount),
                    JournalLine.ToCredit(SystemAccounts.CuentaComitente, amount));
                p.Quantity += remaining;
            }
            else
            {
                Post(date, $"Lanzamiento de {remaining} {q.Instrument}", "entry.option-write", q.Instrument,
                    JournalLine.ToDebit(SystemAccounts.CuentaComitente, amount),
                    JournalLine.ToCredit(SystemAccounts.PrimasLanzadas, amount));
                p.Quantity -= remaining;
            }

            p.PremiumBasis += amount;
        }

        if (p.Quantity == 0)
        {
            _s.Derivatives.OptionPositions.Remove(p);
        }

        PostDerivativeFees(q.Commission, q.Iva, date, q.Instrument);
        AdjustMargin(date, timeOfDay);
        _s.Broker.Trades.Add(new Trade(_s.Broker.NextId++, date, timeOfDay, InstrumentKind.Option, q.Instrument, q.Side, q.Quantity,
            MoneyRounding.Round((decimal)q.UnitPrice), q.Notional, q.Commission + q.Iva, q.Notional));
    }

    /// <summary>Cierra parte de una posición: la prima registrada contra lo que se paga o cobra hoy.</summary>
    private void CloseOptionPart(OptionPosition p, long contracts, decimal cash, EconomicDate date, bool buyingBack)
    {
        long open = Math.Abs(p.Quantity);
        decimal basis = contracts == open ? p.PremiumBasis : MoneyRounding.Round(p.PremiumBasis * contracts / open);
        decimal result = buyingBack ? basis - cash : cash - basis;
        var lines = new List<JournalLine>();
        if (buyingBack)
        {
            lines.Add(JournalLine.ToDebit(SystemAccounts.PrimasLanzadas, basis));
            lines.Add(JournalLine.ToCredit(SystemAccounts.CuentaComitente, cash));
        }
        else
        {
            lines.Add(JournalLine.ToDebit(SystemAccounts.CuentaComitente, cash));
            lines.Add(JournalLine.ToCredit(SystemAccounts.PrimasCompradas, basis));
        }

        AddResult(lines, result);
        if (lines.Count >= 2 && lines.Any(l => l.Debit > 0) && lines.Any(l => l.Credit > 0))
        {
            Post(date, $"Cierre de {contracts} {p.SeriesId}", "entry.option-close", p.SeriesId, lines.ToArray());
        }

        p.PremiumBasis -= basis;
        p.Quantity += buyingBack ? contracts : -contracts;
    }

    // ───────────────────────────── Vencimiento y ejercicio ─────────────────────────────

    /// <summary>Ejercicio automático de las opciones en el dinero que vencen hoy, con entrega de acciones si se puede.</summary>
    public List<MarketNotice> ExpireOptions(EconomicDate date)
    {
        var notices = new List<MarketNotice>();
        List<OptionSeries> expiring = _s.Derivatives.Options.Where(o => o.Expiry <= date).ToList();
        if (expiring.Count == 0)
        {
            return notices;
        }

        ReleaseAllMargin(date);
        foreach (OptionSeries series in expiring)
        {
            if (OptionPositionFor(series.Id) is { } p && p.Quantity != 0)
            {
                notices.Add(Settle(series, p, date));
                _s.Derivatives.OptionPositions.Remove(p);
            }

            _s.Derivatives.Options.Remove(series);
        }

        return notices;
    }

    private MarketNotice Settle(OptionSeries series, OptionPosition p, EconomicDate date)
    {
        // 1) La prima registrada pasa a resultado: pérdida si se compró, ganancia si se lanzó.
        if (p.PremiumBasis > 0)
        {
            if (p.Quantity > 0)
            {
                Post(date, $"Prima de {series.Id}", "entry.option-premium", series.Id,
                    JournalLine.ToDebit(SystemAccounts.ResultadosDerivadosNegativos, p.PremiumBasis),
                    JournalLine.ToCredit(SystemAccounts.PrimasCompradas, p.PremiumBasis));
            }
            else
            {
                Post(date, $"Prima de {series.Id}", "entry.option-premium", series.Id,
                    JournalLine.ToDebit(SystemAccounts.PrimasLanzadas, p.PremiumBasis),
                    JournalLine.ToCredit(SystemAccounts.ResultadosDerivadosPositivos, p.PremiumBasis));
            }
        }

        // 2) Si está en el dinero, se ejerce.
        double spot = _stocks.PriceAt(series.Ticker, MarketHours.Close);
        double intrinsic = series.Type == OptionType.Call ? spot - series.Strike : series.Strike - spot;
        long shares = Math.Abs(p.Quantity) * DerivativesRules.OptionLot;
        if (intrinsic <= 0)
        {
            return new MarketNotice(MarketNoticeKind.OptionExpired, date, series.Id, 0);
        }

        decimal strikeValue = MoneyRounding.Round((decimal)series.Strike * shares);
        decimal payoff = MoneyRounding.Round((decimal)intrinsic * shares);
        bool buysShares = (series.Type == OptionType.Call) == (p.Quantity > 0);
        long available = _s.Broker.Stocks.TryGetValue(series.Ticker, out Holding? h) ? h.Quantity : 0;

        if (buysShares && Cash >= strikeValue)
        {
            BuySharesAt(series.Ticker, shares, strikeValue, date, series.Id);
            return new MarketNotice(MarketNoticeKind.OptionExercised, date, series.Id, shares);
        }

        if (!buysShares && available >= shares)
        {
            SellSharesAt(series.Ticker, shares, strikeValue, date, series.Id);
            return new MarketNotice(MarketNoticeKind.OptionExercised, date, series.Id, -shares);
        }

        // Por diferencia: el comprador cobra el valor intrínseco, el lanzador lo paga.
        if (p.Quantity > 0)
        {
            Post(date, $"Ejercicio por diferencia de {series.Id}", "entry.option-cash", series.Id,
                JournalLine.ToDebit(SystemAccounts.CuentaComitente, payoff),
                JournalLine.ToCredit(SystemAccounts.ResultadosDerivadosPositivos, payoff));
        }
        else
        {
            Post(date, $"Ejercicio por diferencia de {series.Id}", "entry.option-cash", series.Id,
                JournalLine.ToDebit(SystemAccounts.ResultadosDerivadosNegativos, payoff),
                JournalLine.ToCredit(SystemAccounts.CuentaComitente, payoff));
        }

        return new MarketNotice(MarketNoticeKind.OptionExercised, date, series.Id, p.Quantity > 0 ? payoff : -payoff);
    }

    private void BuySharesAt(string ticker, long shares, decimal total, EconomicDate date, string seriesId)
    {
        Post(date, $"Ejercicio de {seriesId}: compra de {shares} {ticker}", "entry.option-exercise-buy", seriesId,
            JournalLine.ToDebit(SystemAccounts.Acciones, total),
            JournalLine.ToCredit(SystemAccounts.CuentaComitente, total));
        _s.Broker.Stocks.TryGetValue(ticker, out Holding? holding);
        holding ??= new Holding();
        holding.Quantity += shares;
        holding.CostBasis += total;
        _s.Broker.Stocks[ticker] = holding;
    }

    private void SellSharesAt(string ticker, long shares, decimal total, EconomicDate date, string seriesId)
    {
        Holding holding = _s.Broker.Stocks[ticker];
        decimal cost = holding.Quantity == shares ? holding.CostBasis : MoneyRounding.Round(holding.CostBasis * shares / holding.Quantity);
        var lines = new List<JournalLine>
        {
            JournalLine.ToDebit(SystemAccounts.CuentaComitente, total),
            JournalLine.ToCredit(SystemAccounts.Acciones, cost),
        };
        decimal result = total - cost;
        if (result > 0)
        {
            lines.Add(JournalLine.ToCredit(SystemAccounts.ResultadosVentaPositivos, result));
        }
        else if (result < 0)
        {
            lines.Add(JournalLine.ToDebit(SystemAccounts.ResultadosVentaNegativos, -result));
        }

        Post(date, $"Ejercicio de {seriesId}: venta de {shares} {ticker}", "entry.option-exercise-sell", seriesId, lines.ToArray());
        holding.Quantity -= shares;
        holding.CostBasis -= cost;
        if (holding.Quantity == 0)
        {
            _s.Broker.Stocks.Remove(ticker);
        }
    }

    private static void AddResult(List<JournalLine> lines, decimal result)
    {
        if (result > 0)
        {
            lines.Add(JournalLine.ToCredit(SystemAccounts.ResultadosDerivadosPositivos, result));
        }
        else if (result < 0)
        {
            lines.Add(JournalLine.ToDebit(SystemAccounts.ResultadosDerivadosNegativos, -result));
        }
    }
}
