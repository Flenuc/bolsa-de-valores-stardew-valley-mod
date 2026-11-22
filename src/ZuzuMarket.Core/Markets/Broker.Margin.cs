using ZuzuMarket.Core.Ledger;
using ZuzuMarket.Core.Money;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Markets;

/// <summary>Garantías de futuros y opciones lanzadas: requerimientos, reposición, llamadas de margen y liquidación forzosa.</summary>
public sealed partial class Broker
{
    /// <summary>Saldo depositado en garantía.</summary>
    public decimal MarginBalance => _ledger.GetBalance(LedgerEntities.Jugador, SystemAccounts.Garantias);

    /// <summary>
    /// Garantía inicial requerida por todas las posiciones. Con <paramref name="overrideId"/> se calcula como si esa
    /// posición tuviera <paramref name="overrideQuantity"/> (para cotizar una operación).
    /// </summary>
    public decimal RequiredMargin(EconomicDate date, int timeOfDay, string? overrideId = null, long overrideQuantity = 0)
    {
        double total = 0;
        double imRate = DerivativesRules.InitialMargin * (double)_difficulty().PriceVolatility;

        var futures = _s.Derivatives.FuturesPositions.ToDictionary(p => p.ContractId, p => p.Quantity);
        var options = _s.Derivatives.OptionPositions.ToDictionary(p => p.SeriesId, p => p.Quantity);
        if (overrideId is not null)
        {
            if (FindFuture(overrideId) is not null)
            {
                futures[overrideId] = overrideQuantity;
            }
            else
            {
                options[overrideId] = overrideQuantity;
            }
        }

        foreach ((string id, long qty) in futures)
        {
            if (qty != 0 && FindFuture(id) is { } contract)
            {
                total += Math.Abs(qty) * ContractSize(contract) * FuturePrice(contract, date) * imRate;
            }
        }

        // Calls lanzados: los cubiertos con acciones propias no requieren garantía en oro.
        foreach (IGrouping<string, KeyValuePair<string, long>> byTicker in options
            .Where(kv => kv.Value < 0 && FindOption(kv.Key) is not null)
            .GroupBy(kv => FindOption(kv.Key)!.Ticker))
        {
            long coverLots = (_s.Broker.Stocks.TryGetValue(byTicker.Key, out Holding? h) ? h.Quantity : 0) / DerivativesRules.OptionLot;
            foreach ((string id, long qty) in byTicker.OrderBy(kv => FindOption(kv.Key)!.Strike))
            {
                OptionSeries series = FindOption(id)!;
                long lots = -qty;
                if (series.Type == OptionType.Call)
                {
                    long covered = Math.Min(lots, coverLots);
                    coverLots -= covered;
                    lots -= covered;
                }

                if (lots > 0)
                {
                    total += lots * DerivativesRules.OptionLot * ShortOptionMarginPerShare(series, date, timeOfDay);
                }
            }
        }

        return MoneyRounding.Round((decimal)total);
    }

    /// <summary>Garantía por acción de una opción lanzada descubierta: prima + 20% del subyacente − fuera del dinero (mínimo 10%).</summary>
    public double ShortOptionMarginPerShare(OptionSeries series, EconomicDate date, int timeOfDay)
    {
        double spot = _stocks.PriceAt(series.Ticker, timeOfDay);
        double outOfMoney = Math.Max(0, series.Type == OptionType.Call ? series.Strike - spot : spot - series.Strike);
        double premium = OptionPremium(series, date, timeOfDay);
        return premium + Math.Max((DerivativesRules.ShortOptionMarginRate * spot) - outOfMoney, DerivativesRules.ShortOptionMinimumRate * spot);
    }

    /// <summary>Acciones bloqueadas como cobertura de calls lanzados.</summary>
    public long BlockedShares(string ticker)
    {
        long shortCallLots = _s.Derivatives.OptionPositions
            .Where(p => p.Quantity < 0 && FindOption(p.SeriesId) is { Type: OptionType.Call } s && s.Ticker == ticker)
            .Sum(p => -p.Quantity);
        long held = _s.Broker.Stocks.TryGetValue(ticker, out Holding? h) ? h.Quantity : 0;
        return Math.Min(held, shortCallLots * DerivativesRules.OptionLot);
    }

    public bool HasDerivativePositions => _s.Derivatives.FuturesPositions.Count > 0 || _s.Derivatives.OptionPositions.Count > 0;

    /// <summary>Deja en garantía exactamente lo requerido: repone desde la cuenta comitente (si alcanza) o devuelve el exceso.</summary>
    public void AdjustMargin(EconomicDate date, int timeOfDay)
    {
        decimal required = RequiredMargin(date, timeOfDay);
        decimal balance = MarginBalance;
        if (balance < required)
        {
            decimal transfer = Math.Min(required - balance, Math.Max(0, Cash));
            if (transfer > 0)
            {
                Post(date, "Depósito de garantías", "entry.margin-deposit", "",
                    JournalLine.ToDebit(SystemAccounts.Garantias, transfer),
                    JournalLine.ToCredit(SystemAccounts.CuentaComitente, transfer));
            }
        }
        else if (balance > required)
        {
            Post(date, "Devolución de garantías", "entry.margin-release", "",
                JournalLine.ToDebit(SystemAccounts.CuentaComitente, balance - required),
                JournalLine.ToCredit(SystemAccounts.Garantias, balance - required));
        }
    }

    /// <summary>Devuelve todo lo depositado en garantía (antes de vencimientos; después se vuelve a exigir lo que corresponda).</summary>
    public void ReleaseAllMargin(EconomicDate date)
    {
        decimal balance = MarginBalance;
        if (balance > 0)
        {
            Post(date, "Devolución de garantías", "entry.margin-release", "",
                JournalLine.ToDebit(SystemAccounts.CuentaComitente, balance),
                JournalLine.ToCredit(SystemAccounts.Garantias, balance));
        }
        else if (balance < 0)
        {
            Post(date, "Saldo negativo de garantías", "entry.margin-deposit", "",
                JournalLine.ToDebit(SystemAccounts.Garantias, -balance),
                JournalLine.ToCredit(SystemAccounts.CuentaComitente, -balance));
        }
    }

    /// <summary>
    /// Control diario: si la garantía quedó por debajo de la mínima se repone sola desde la cuenta comitente; si no
    /// alcanza, llamada de margen hasta el día hábil siguiente; si sigue sin cubrirse, liquidación forzosa.
    /// </summary>
    public List<MarketNotice> CheckMargin(EconomicDate date, int timeOfDay = MarketHours.Close)
    {
        var notices = new List<MarketNotice>();
        DerivativesState d = _s.Derivatives;
        decimal required = RequiredMargin(date, timeOfDay);
        decimal maintenance = MoneyRounding.Round(required * (decimal)DerivativesRules.MaintenanceRatio);
        decimal before = MarginBalance;

        if (before >= maintenance)
        {
            if (before > required)
            {
                AdjustMargin(date, timeOfDay);
            }

            d.MarginCallDeadline = null;
            return notices;
        }

        AdjustMargin(date, timeOfDay);
        decimal after = MarginBalance;
        if (after >= maintenance)
        {
            notices.Add(new MarketNotice(MarketNoticeKind.MarginTopUp, date, "", after - before));
            d.MarginCallDeadline = null;
            return notices;
        }

        if (d.MarginCallDeadline is EconomicDate deadline && date >= deadline)
        {
            Liquidate(date, timeOfDay);
            d.MarginCallDeadline = null;
            notices.Add(new MarketNotice(MarketNoticeKind.Liquidation, date, "", MarginBalance));
            return notices;
        }

        if (d.MarginCallDeadline is null)
        {
            d.MarginCallDeadline = date.AddBusinessDays(1);
            notices.Add(new MarketNotice(MarketNoticeKind.MarginCall, date, "", required - after));
        }

        return notices;
    }

    /// <summary>Liquidación forzosa: cierra futuros y opciones lanzadas al precio del momento, sin validar horario ni saldo.</summary>
    public void Liquidate(EconomicDate date, int timeOfDay)
    {
        foreach (FuturesPosition p in _s.Derivatives.FuturesPositions.ToList())
        {
            TradeSide side = p.Quantity > 0 ? TradeSide.Sell : TradeSide.Buy;
            DerivativeQuote q = QuoteFuture(p.ContractId, side, Math.Abs(p.Quantity), date, timeOfDay) with { Error = TradeError.None };
            ApplyFutureTrade(q, date, timeOfDay);
        }

        foreach (OptionPosition p in _s.Derivatives.OptionPositions.Where(p => p.Quantity < 0).ToList())
        {
            DerivativeQuote q = QuoteOption(p.SeriesId, TradeSide.Buy, -p.Quantity, date, timeOfDay) with { Error = TradeError.None };
            ApplyOptionTrade(q, date, timeOfDay);
        }

        ReleaseAllMargin(date);
    }

    private TradeError ValidateDerivative(long quantity, EconomicDate date, int timeOfDay, decimal cashNeeded)
    {
        if (!IsOpen)
        {
            return TradeError.NoAccount;
        }

        if (quantity <= 0)
        {
            return TradeError.InvalidQuantity;
        }

        if (!IsMarketOpen(date, timeOfDay))
        {
            return TradeError.MarketClosed;
        }

        return cashNeeded > Cash ? TradeError.InsufficientFunds : TradeError.None;
    }
}
