using System.Globalization;
using ZuzuMarket.Core.Commodities;
using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Events;
using ZuzuMarket.Core.Ledger;
using ZuzuMarket.Core.Macro;
using ZuzuMarket.Core.Money;
using ZuzuMarket.Core.Taxation;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Markets;

public enum TradeError
{
    None,
    NoAccount,
    MarketClosed,
    UnknownInstrument,
    InvalidQuantity,
    InsufficientFunds,
    InsufficientHoldings,
    BelowMinimum,
    InvalidTerm,

    /// <summary>Embargo, concurso o inhabilitación: solo se pueden cerrar posiciones.</summary>
    Inhibited,

    /// <summary>Cuenta comitente embargada: no se puede retirar.</summary>
    AccountFrozen,

    AboveLimit,

    /// <summary>Es la acción de tu propia sociedad: solo recompra o colocación desde la pestaña Empresas.</summary>
    OwnCompany,
}

/// <summary>Cotización de una orden de mercado con todos sus costos.</summary>
public sealed record TradeQuote(
    InstrumentKind Kind,
    string Instrument,
    TradeSide Side,
    long Quantity,
    decimal UnitPrice,
    decimal Gross,
    decimal Commission,
    decimal MarketFee,
    decimal Iva,
    TradeError Error)
{
    public decimal Fees => Commission + MarketFee + Iva;

    /// <summary>Compra: lo que se debita de la cuenta. Venta: lo que se acredita.</summary>
    public decimal Total => Side == TradeSide.Buy ? Gross + Fees : Gross - Fees;

    public bool IsValid => Error == TradeError.None;
}

/// <summary>
/// ALyC Valle Inversiones: cuenta comitente, compra y venta de acciones y Letras, cauciones colocadoras,
/// dividendos y vencimientos. Todo se asienta en el libro de la entidad <see cref="LedgerEntities.Jugador"/>.
/// </summary>
public sealed partial class Broker
{
    private const string Entity = LedgerEntities.Jugador;

    private readonly GameData _data;
    private readonly MarketsState _s;
    private readonly GeneralLedger _ledger;
    private readonly StockMarket _stocks;
    private readonly CentralBank _centralBank;
    private readonly CreditEngine _credit;
    private readonly InflationModel _inflation;
    private readonly Func<TaxRegime> _regime;
    private readonly CommodityMarket _commodities;
    private readonly EventEngine _events;
    private readonly TaxEngine _taxes;
    private readonly Func<DifficultyDefinition> _difficulty;

    public Broker(GameData data, MarketsState state, GeneralLedger ledger, StockMarket stocks, CentralBank centralBank, CreditEngine credit, InflationModel inflation, CommodityMarket commodities, EventEngine events, TaxEngine taxes, Func<DifficultyDefinition> difficulty)
    {
        _commodities = commodities;
        _events = events;
        _taxes = taxes;
        _difficulty = difficulty;
        _regime = () => taxes.Regime;
        _data = data;
        _s = state;
        _ledger = ledger;
        _stocks = stocks;
        _centralBank = centralBank;
        _credit = credit;
        _inflation = inflation;
    }

    /// <summary>Inhibición (la fija el motor legal): no se abren posiciones nuevas.</summary>
    public Func<bool> IsInhibited { get; set; } = () => false;

    /// <summary>Cuenta comitente embargada (la fija el motor legal): no se puede retirar.</summary>
    public Func<bool> IsAccountFrozen { get; set; } = () => false;

    /// <summary>Descuento sobre las comisiones de la ALyC (0 a 1), p. ej. por la amistad con tu agente de bolsa.</summary>
    public Func<decimal> CommissionDiscount { get; set; } = () => 0;

    private decimal CommissionFactor => 1 - Math.Clamp(CommissionDiscount(), 0, 1);

    /// <summary>Ticker de una sociedad del jugador (la fija el motor de sociedades).</summary>
    public Func<string, bool> IsOwnCompany { get; set; } = _ => false;

    /// <summary>
    /// Servicio de ON emitidas por una sociedad del jugador: la sociedad paga el flujo por lámina × láminas emitidas.
    /// Devuelve false si no pudo pagar (la sociedad quiebra y la serie entra en default).
    /// </summary>
    public Func<BondSeries, BondCashFlow, EconomicDate, bool>? PayIssuer { get; set; }

    /// <summary>Durante una venta forzosa (caución impaga, quiebra) se ignoran el horario y la inhibición.</summary>
    private bool _forcing;

    public BrokerState State => _s.Broker;

    public bool IsOpen => _s.Broker.Opened;

    public MarketRules Rules => _data.Markets;

    /// <summary>Saldo disponible en la cuenta comitente.</summary>
    public decimal Cash => _ledger.GetBalance(Entity, SystemAccounts.CuentaComitente);

    // ───────────────────────────── Tasas ─────────────────────────────

    /// <summary>Tasa de referencia mensual (por 28 días): la tasa de política del BCRF.</summary>
    public double ReferenceRate => Math.Max(Rules.MinimumRate, _centralBank.PolicyRate);

    public double CaucionRate => ReferenceRate * Rules.CaucionRateFactor;

    /// <summary>Curva: tasa de política + prima por plazo + spread de crédito del emisor (mensual).</summary>
    public double Yield(int days, string issuer) => ReferenceRate + Rules.Premium(days) + _credit.Spread(issuer);

    /// <summary>Riesgo país en puntos básicos anuales.</summary>
    public double CountryRiskBps => _credit.CountryRiskBps;

    public double LetraRate(LetraSeries series) => Yield(series.TenorDays, CreditEngine.Sovereign);

    // ───────────────────────────── Cuenta ─────────────────────────────

    public void Open(EconomicDate date)
    {
        if (!_s.Broker.Opened)
        {
            _s.Broker.Opened = true;
            _s.Broker.OpenedOn = date;
        }
    }

    /// <summary>Pasa oro de la Caja a la cuenta comitente. El juego debe descontar el oro al jugador.</summary>
    public TradeError Deposit(decimal amount, EconomicDate date)
    {
        if (!IsOpen)
        {
            return TradeError.NoAccount;
        }

        if (amount <= 0)
        {
            return TradeError.InvalidQuantity;
        }

        _ledger.Post(Entity, date, new EntryText("Depósito en la cuenta comitente", "entry.broker-deposit"), EntrySources.Ajuste,
            JournalLine.ToDebit(SystemAccounts.CuentaComitente, amount),
            JournalLine.ToCredit(SystemAccounts.Caja, amount));
        return TradeError.None;
    }

    /// <summary>Pasa oro de la cuenta comitente a la Caja. El juego debe sumar el oro al jugador.</summary>
    public TradeError Withdraw(decimal amount, EconomicDate date)
    {
        if (!IsOpen)
        {
            return TradeError.NoAccount;
        }

        if (amount <= 0)
        {
            return TradeError.InvalidQuantity;
        }

        if (IsAccountFrozen())
        {
            return TradeError.AccountFrozen;
        }

        if (amount > Cash)
        {
            return TradeError.InsufficientFunds;
        }

        _ledger.Post(Entity, date, new EntryText("Retiro de la cuenta comitente", "entry.broker-withdraw"), EntrySources.Ajuste,
            JournalLine.ToDebit(SystemAccounts.Caja, amount),
            JournalLine.ToCredit(SystemAccounts.CuentaComitente, amount));
        return TradeError.None;
    }

    // ───────────────────────────── Letras ─────────────────────────────

    public IReadOnlyList<LetraSeries> ActiveLetras(EconomicDate date) =>
        _s.Letras.Where(l => l.Maturity > date).OrderBy(l => l.Maturity).ThenBy(l => l.Id).ToList();

    public LetraSeries? FindLetra(string id) =>
        _s.Letras.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Licitación: una serie por cada plazo, emitida hoy.</summary>
    public MarketNotice IssueLetras(EconomicDate date)
    {
        foreach (LetraTenor tenor in Rules.LetraTenors)
        {
            string id = $"LEFE{tenor.Days}-{SeasonLetter(date.Season)}{date.Year}";
            if (FindLetra(id) is null)
            {
                _s.Letras.Add(new LetraSeries
                {
                    Id = id,
                    TenorDays = tenor.Days,
                    IssueDate = date,
                    Maturity = date.AddDays(tenor.Days).NextBusinessDayOrSelf(),
                });
            }
        }

        return new MarketNotice(MarketNoticeKind.LetrasIssued, date, "", 0);
    }

    /// <summary>Precio teórico de una lámina de VN 100: VN / (1 + tasa × días / 28).</summary>
    public double LetraPrice(LetraSeries series, EconomicDate date)
    {
        int days = Math.Max(0, date.DaysUntil(series.Maturity));
        return (double)Rules.LetraFaceValue / (1 + (LetraRate(series) * days / EconomicDate.DaysPerSeason));
    }

    // ───────────────────────────── Órdenes ─────────────────────────────

    public static bool IsMarketOpen(EconomicDate date, int timeOfDay) => MarketHours.IsOpen(date, timeOfDay);

    /// <summary>Precio medio vigente (sin spread) de una acción o una Letra.</summary>
    public double? MidPrice(string instrument, EconomicDate date, int timeOfDay)
    {
        if (_stocks.Find(instrument) is { } company)
        {
            return _stocks.PriceAt(company.Ticker, timeOfDay);
        }

        if (FindBond(instrument) is { } bond)
        {
            return BondPrice(bond, date);
        }

        return FindLetra(instrument) is { } letra && letra.Maturity > date ? LetraPrice(letra, date) : null;
    }

    private (InstrumentKind Kind, string Id) Resolve(string instrument)
    {
        if (_stocks.Find(instrument) is { } company)
        {
            return (InstrumentKind.Stock, company.Ticker);
        }

        if (FindBond(instrument) is { } bond)
        {
            return (InstrumentKind.Bond, bond.Id);
        }

        return (InstrumentKind.Letra, FindLetra(instrument)?.Id ?? instrument);
    }

    private Dictionary<string, Holding> Book(InstrumentKind kind) => kind switch
    {
        InstrumentKind.Stock => _s.Broker.Stocks,
        InstrumentKind.Bond => _s.Broker.Bonds,
        _ => _s.Broker.Letras,
    };

    private static string AssetAccount(InstrumentKind kind) => kind switch
    {
        InstrumentKind.Stock => SystemAccounts.Acciones,
        InstrumentKind.Bond => SystemAccounts.Bonos,
        _ => SystemAccounts.Letras,
    };

    /// <summary>Cotiza una orden de mercado. Devuelve los costos aunque haya un error, para mostrarlos.</summary>
    public TradeQuote Quote(string instrument, TradeSide side, long quantity, EconomicDate date, int timeOfDay)
    {
        CompanyDefinition? company = _stocks.Find(instrument);
        (InstrumentKind kind, string id) = Resolve(instrument);
        double? mid = MidPrice(id, date, timeOfDay);
        if (mid is null)
        {
            return new TradeQuote(kind, id, side, quantity, 0, 0, 0, 0, 0, TradeError.UnknownInstrument);
        }

        double spread = kind switch
        {
            InstrumentKind.Stock => Rules.StockSpread,
            InstrumentKind.Bond => Rules.BondSpread,
            _ => Rules.LetraSpread,
        };
        double impact = company is null ? 0 : Math.Min(Rules.MaxImpact, Rules.ImpactCoefficient * quantity / Math.Max(1, company.AverageVolume));
        double sign = side == TradeSide.Buy ? 1 : -1;
        decimal unit = MoneyRounding.Round((decimal)(mid.Value * (1 + (sign * ((spread / 2) + impact)))));
        decimal gross = unit * Math.Max(0, quantity);
        decimal commission = gross > 0 ? MoneyRounding.Round(Math.Max(gross * Rules.CommissionRate * CommissionFactor, Rules.MinimumCommission)) : 0;
        decimal marketFee = MoneyRounding.Round(gross * Rules.MarketFeeRate);
        decimal iva = MoneyRounding.Round(commission * Rules.CommissionIvaRate);

        TradeError error = Validate(kind, id, side, quantity, date, timeOfDay, gross + commission + marketFee + iva);
        return new TradeQuote(kind, id, side, quantity, unit, gross, commission, marketFee, iva, error);
    }

    /// <summary>Ejecuta una orden de mercado al precio vigente.</summary>
    public TradeQuote Execute(string instrument, TradeSide side, long quantity, EconomicDate date, int timeOfDay)
    {
        TradeQuote q = Quote(instrument, side, quantity, date, timeOfDay);
        if (!q.IsValid)
        {
            return q;
        }

        Dictionary<string, Holding> book = Book(q.Kind);
        string assetAccount = AssetAccount(q.Kind);
        bool ri = _regime() == TaxRegime.ResponsableInscripto;
        decimal feeExpense = q.Commission + q.MarketFee + (ri ? 0 : q.Iva);
        var args = new Dictionary<string, string>
        {
            ["instrument"] = q.Instrument,
            ["quantity"] = q.Quantity.ToString(CultureInfo.InvariantCulture),
        };
        var lines = new List<JournalLine>();

        if (q.Side == TradeSide.Buy)
        {
            lines.Add(JournalLine.ToDebit(assetAccount, q.Gross));
            AddFees(lines, feeExpense, ri ? q.Iva : 0);
            lines.Add(JournalLine.ToCredit(SystemAccounts.CuentaComitente, q.Total));

            book.TryGetValue(q.Instrument, out Holding? holding);
            holding ??= new Holding();
            holding.CostBasis += q.Gross;
            holding.Quantity += q.Quantity;
            book[q.Instrument] = holding;
            _ledger.Post(Entity, date, new EntryText($"Compra de {q.Quantity} {q.Instrument}", "entry.broker-buy", args), EntrySources.Compra, lines.ToArray());
        }
        else
        {
            Holding holding = book[q.Instrument];
            decimal cost = holding.Quantity == q.Quantity ? holding.CostBasis : MoneyRounding.Round(holding.CostBasis * q.Quantity / holding.Quantity);
            decimal result = q.Gross - cost;
            lines.Add(JournalLine.ToDebit(SystemAccounts.CuentaComitente, q.Total));
            AddFees(lines, feeExpense, ri ? q.Iva : 0);
            lines.Add(JournalLine.ToCredit(assetAccount, cost));
            if (result > 0)
            {
                lines.Add(JournalLine.ToCredit(SystemAccounts.ResultadosVentaPositivos, result));
            }
            else if (result < 0)
            {
                lines.Add(JournalLine.ToDebit(SystemAccounts.ResultadosVentaNegativos, -result));
            }

            holding.Quantity -= q.Quantity;
            holding.CostBasis -= cost;
            if (holding.Quantity == 0)
            {
                book.Remove(q.Instrument);
            }

            _ledger.Post(Entity, date, new EntryText($"Venta de {q.Quantity} {q.Instrument}", "entry.broker-sell", args), EntrySources.Venta, lines.ToArray());
        }

        _s.Broker.Trades.Add(new Trade(_s.Broker.NextId++, date, timeOfDay, q.Kind, q.Instrument, q.Side, q.Quantity, q.UnitPrice, q.Gross, q.Fees, q.Total));
        return q;
    }

    // ───────────────────────────── Cauciones ─────────────────────────────

    /// <summary>Interés de una caución colocada hoy: capital × tasa × días reales / 28.</summary>
    public (EconomicDate Maturity, decimal Interest) CaucionTerms(decimal amount, int days, EconomicDate date)
    {
        EconomicDate maturity = date.AddDays(Math.Max(1, days)).NextBusinessDayOrSelf();
        decimal interest = MoneyRounding.Round(amount * (decimal)(CaucionRate * date.DaysUntil(maturity) / EconomicDate.DaysPerSeason));
        return (maturity, interest);
    }

    public TradeError ValidateCaucion(decimal amount, int days, EconomicDate date, int timeOfDay)
    {
        if (!IsOpen)
        {
            return TradeError.NoAccount;
        }

        if (!IsMarketOpen(date, timeOfDay))
        {
            return TradeError.MarketClosed;
        }

        if (!Rules.CaucionTerms.Contains(days))
        {
            return TradeError.InvalidTerm;
        }

        if (amount < Rules.CaucionMinimum)
        {
            return TradeError.BelowMinimum;
        }

        return amount > Cash ? TradeError.InsufficientFunds : TradeError.None;
    }

    public TradeError PlaceCaucion(decimal amount, int days, EconomicDate date, int timeOfDay)
    {
        TradeError error = ValidateCaucion(amount, days, date, timeOfDay);
        if (error != TradeError.None)
        {
            return error;
        }

        (EconomicDate maturity, decimal interest) = CaucionTerms(amount, days, date);
        var caucion = new Caucion
        {
            Id = _s.Broker.NextId++,
            Principal = amount,
            Rate = CaucionRate,
            Start = date,
            Maturity = maturity,
            Interest = interest,
        };
        _s.Broker.Cauciones.Add(caucion);
        _ledger.Post(Entity, date,
            new EntryText($"Caución colocadora a {days} días", "entry.caucion-placed", new Dictionary<string, string> { ["days"] = days.ToString(CultureInfo.InvariantCulture) }),
            EntrySources.Ajuste,
            JournalLine.ToDebit(SystemAccounts.CaucionesColocadas, amount),
            JournalLine.ToCredit(SystemAccounts.CuentaComitente, amount));
        return TradeError.None;
    }

    // ───────────────────────────── Eventos diarios ─────────────────────────────

    /// <summary>Cobra las Letras y cauciones que vencen hoy (o antes, si se saltearon días).</summary>
    public List<MarketNotice> OnDay(EconomicDate date)
    {
        var notices = new List<MarketNotice>();
        foreach (Caucion c in _s.Broker.Cauciones.Where(c => c.Maturity <= date).ToList())
        {
            var lines = new List<JournalLine>
            {
                JournalLine.ToDebit(SystemAccounts.CuentaComitente, c.Principal + c.Interest),
                JournalLine.ToCredit(SystemAccounts.CaucionesColocadas, c.Principal),
            };
            if (c.Interest > 0)
            {
                lines.Add(JournalLine.ToCredit(SystemAccounts.InteresesGanados, c.Interest));
            }

            _ledger.Post(Entity, date, new EntryText("Cobro de caución", "entry.caucion-collected"), EntrySources.Cobro, lines.ToArray());
            _s.Broker.Cauciones.Remove(c);
            notices.Add(new MarketNotice(MarketNoticeKind.CaucionMatured, date, c.Id.ToString(CultureInfo.InvariantCulture), c.Principal + c.Interest));
        }

        foreach (LetraSeries letra in _s.Letras.Where(l => l.Maturity <= date).ToList())
        {
            if (_s.Broker.Letras.TryGetValue(letra.Id, out Holding? holding))
            {
                decimal face = Rules.LetraFaceValue * holding.Quantity;
                decimal interest = face - holding.CostBasis;
                var lines = new List<JournalLine>
                {
                    JournalLine.ToDebit(SystemAccounts.CuentaComitente, face),
                    JournalLine.ToCredit(SystemAccounts.Letras, holding.CostBasis),
                };
                if (interest > 0)
                {
                    lines.Add(JournalLine.ToCredit(SystemAccounts.InteresesGanados, interest));
                }
                else if (interest < 0)
                {
                    lines.Add(JournalLine.ToDebit(SystemAccounts.ResultadosVentaNegativos, -interest));
                }

                _ledger.Post(Entity, date,
                    new EntryText($"Vencimiento de {letra.Id}", "entry.letra-matured", new Dictionary<string, string> { ["instrument"] = letra.Id }),
                    EntrySources.Cobro, lines.ToArray());
                _s.Broker.Letras.Remove(letra.Id);
                notices.Add(new MarketNotice(MarketNoticeKind.LetraMatured, date, letra.Id, face));
            }

            _s.Letras.Remove(letra);
        }

        return notices;
    }

    /// <summary>Dividendos trimestrales de las acciones en cartera, con la retención del 7%.</summary>
    public List<MarketNotice> PayDividends(EconomicDate date)
    {
        var notices = new List<MarketNotice>();
        foreach ((string ticker, Holding holding) in _s.Broker.Stocks.ToList())
        {
            CompanyDefinition? company = _stocks.Find(ticker);
            if (company is null || holding.Quantity <= 0)
            {
                continue;
            }

            decimal gross = MoneyRounding.Round((decimal)_stocks.QuarterlyDividend(company) * holding.Quantity);
            if (gross <= 0)
            {
                continue;
            }

            decimal withheld = MoneyRounding.Round(gross * Rules.DividendWithholding);
            var lines = new List<JournalLine> { JournalLine.ToDebit(SystemAccounts.CuentaComitente, gross - withheld) };
            if (withheld > 0)
            {
                lines.Add(JournalLine.ToDebit(SystemAccounts.RetencionDividendos, withheld));
            }

            lines.Add(JournalLine.ToCredit(SystemAccounts.Dividendos, gross));
            _ledger.Post(Entity, date,
                new EntryText($"Dividendo de {ticker}", "entry.dividend", new Dictionary<string, string> { ["instrument"] = ticker }),
                EntrySources.Cobro, lines.ToArray());
            notices.Add(new MarketNotice(MarketNoticeKind.Dividend, date, ticker, gross - withheld));
        }

        return notices;
    }

    // ───────────────────────────── Valuación ─────────────────────────────

    /// <summary>Valor de mercado de las tenencias (acciones y Letras al precio medio, cauciones con su interés devengado).</summary>
    /// <summary>Guarda el valor de la cartera al cierre (para el gráfico).</summary>
    public void RecordValue(EconomicDate date)
    {
        if (!IsOpen || !date.IsBusinessDay)
        {
            return;
        }

        _s.Broker.ValueHistory.Add(new IndexClose(date, (double)PortfolioValue(date, MarketHours.Close)));
        if (_s.Broker.ValueHistory.Count > EconomicDate.DaysPerYear)
        {
            _s.Broker.ValueHistory.RemoveAt(0);
        }
    }

    public decimal PortfolioValue(EconomicDate date, int timeOfDay)
    {
        double stocks = _s.Broker.Stocks.Sum(kv => (_stocks.Find(kv.Key) is null ? 0 : _stocks.PriceAt(kv.Key, timeOfDay)) * kv.Value.Quantity);
        double letras = _s.Broker.Letras.Sum(kv => (FindLetra(kv.Key) is { } l ? LetraPrice(l, date) : 0) * kv.Value.Quantity);
        double bonds = _s.Broker.Bonds.Sum(kv => (FindBond(kv.Key) is { } b ? BondPrice(b, date) : 0) * kv.Value.Quantity);
        double options = _s.Derivatives.OptionPositions.Sum(p =>
            FindOption(p.SeriesId) is { } s ? OptionPremium(s, date, timeOfDay) * DerivativesRules.OptionLot * p.Quantity : 0);
        double futures = _s.Derivatives.FuturesPositions.Sum(p =>
            FindFuture(p.ContractId) is { } f ? (FuturePrice(f, date) - p.LastSettle) * ContractSize(f) * p.Quantity : 0);
        decimal cauciones = _s.Broker.Cauciones.Sum(c => c.Principal);
        return MoneyRounding.Round((decimal)(stocks + letras + bonds + options + futures)) + cauciones + MarginBalance;
    }

    private TradeError Validate(InstrumentKind kind, string id, TradeSide side, long quantity, EconomicDate date, int timeOfDay, decimal buyTotal)
    {
        if (!IsOpen)
        {
            return TradeError.NoAccount;
        }

        if (quantity <= 0)
        {
            return TradeError.InvalidQuantity;
        }

        if (kind == InstrumentKind.Stock && IsOwnCompany(id))
        {
            return TradeError.OwnCompany;
        }

        if (!_forcing && !IsMarketOpen(date, timeOfDay))
        {
            return TradeError.MarketClosed;
        }

        if (side == TradeSide.Buy)
        {
            if (!_forcing && IsInhibited())
            {
                return TradeError.Inhibited;
            }

            return buyTotal > Cash ? TradeError.InsufficientFunds : TradeError.None;
        }

        Dictionary<string, Holding> book = Book(kind);
        long available = book.TryGetValue(id, out Holding? holding) ? holding.Quantity : 0;
        if (kind == InstrumentKind.Stock)
        {
            available -= BlockedShares(id);
        }

        return available >= quantity ? TradeError.None : TradeError.InsufficientHoldings;
    }

    private static void AddFees(List<JournalLine> lines, decimal expense, decimal ivaCredit)
    {
        if (expense > 0)
        {
            lines.Add(JournalLine.ToDebit(SystemAccounts.ComisionesBursatiles, expense));
        }

        if (ivaCredit > 0)
        {
            lines.Add(JournalLine.ToDebit(SystemAccounts.IvaCreditoFiscal, ivaCredit));
        }
    }

    private static string SeasonLetter(Season season) => season switch
    {
        Season.Spring => "P",
        Season.Summer => "V",
        Season.Fall => "O",
        _ => "I",
    };
}
