using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Markets;

/// <summary>Cierre diario de una acción.</summary>
public sealed record StockClose(EconomicDate Date, double Close, double Volume);

/// <summary>Cierre diario del MERZU.</summary>
public sealed record IndexClose(EconomicDate Date, double Value);

public sealed class StockState
{
    public double Sentiment { get; set; }

    /// <summary>Utilidad por acción anual vigente (para el dividendo).</summary>
    public double Eps { get; set; }

    /// <summary>Cierre del día anterior (= apertura de hoy).</summary>
    public double PreviousClose { get; set; }

    /// <summary>Cierre de hoy (el precio de las 17:00).</summary>
    public double Close { get; set; }

    public double Volume { get; set; }

    /// <summary>Camino intradiario de hoy: un precio por tick (el último es el cierre).</summary>
    public List<double> Intraday { get; set; } = new();

    public List<StockClose> History { get; set; } = new();

    public double DailyChange => PreviousClose > 0 ? (Close / PreviousClose) - 1 : 0;
}

/// <summary>Serie de Letras del Tesoro de Ferngill (cupón cero).</summary>
public sealed class LetraSeries
{
    public string Id { get; set; } = "";

    public int TenorDays { get; set; }

    public EconomicDate IssueDate { get; set; }

    public EconomicDate Maturity { get; set; }
}

/// <summary>Tenencia de un título con su costo de compra total (sin comisiones), igual al saldo en el libro.</summary>
public sealed class Holding
{
    public long Quantity { get; set; }

    public decimal CostBasis { get; set; }

    public decimal AverageCost => Quantity > 0 ? CostBasis / Quantity : 0;
}

public sealed class Caucion
{
    public long Id { get; set; }

    public decimal Principal { get; set; }

    /// <summary>Tasa mensual (por 28 días).</summary>
    public double Rate { get; set; }

    public EconomicDate Start { get; set; }

    public EconomicDate Maturity { get; set; }

    public decimal Interest { get; set; }
}

public enum TradeSide
{
    Buy,
    Sell,
}

public enum InstrumentKind
{
    Stock,
    Letra,
    Bond,
    Future,
    Option,
}

public sealed record Trade(
    long Id,
    EconomicDate Date,
    int TimeOfDay,
    InstrumentKind Kind,
    string Instrument,
    TradeSide Side,
    long Quantity,
    decimal Price,
    decimal Gross,
    decimal Fees,
    decimal Net);

/// <summary>Cuenta comitente en Valle Inversiones. El saldo en oro está en el libro (cuenta comitente).</summary>
public sealed class BrokerState
{
    public bool Opened { get; set; }

    public EconomicDate? OpenedOn { get; set; }

    public Dictionary<string, Holding> Stocks { get; set; } = new();

    public Dictionary<string, Holding> Letras { get; set; } = new();

    /// <summary>Bonos soberanos y obligaciones negociables.</summary>
    public Dictionary<string, Holding> Bonds { get; set; } = new();

    public List<Caucion> Cauciones { get; set; } = new();

    /// <summary>Cauciones tomadoras: oro prestado con los títulos en garantía.</summary>
    public List<Caucion> Repos { get; set; } = new();

    public List<Trade> Trades { get; set; } = new();

    /// <summary>Valor total de la cartera al cierre de cada día hábil (último año).</summary>
    public List<IndexClose> ValueHistory { get; set; } = new();

    public long NextId { get; set; } = 1;
}

public sealed class MarketsState
{
    public bool Started { get; set; }

    public EconomicDate? StartedOn { get; set; }

    public Dictionary<string, StockState> Stocks { get; set; } = new();

    /// <summary>Capitalización total el día base del MERZU.</summary>
    public double MerzuBaseCap { get; set; }

    public double Merzu { get; set; }

    public double MerzuPrevious { get; set; }

    public List<IndexClose> MerzuHistory { get; set; } = new();

    public List<LetraSeries> Letras { get; set; } = new();

    public BrokerState Broker { get; set; } = new();

    public bool BondsStarted { get; set; }

    public List<BondSeries> Bonds { get; set; } = new();

    /// <summary>Calificación y situación de cada emisor (el soberano y las empresas).</summary>
    public Dictionary<string, IssuerState> Issuers { get; set; } = new();

    public DerivativesState Derivatives { get; set; } = new();
}

public enum MarketNoticeKind
{
    Dividend,
    LetraMatured,
    CaucionMatured,
    LetrasIssued,
    Coupon,
    BondMatured,
    Exchange,
    MarginTopUp,
    MarginCall,
    Liquidation,
    FuturesDelivered,
    FuturesShortfall,
    FuturesReceived,
    OptionExercised,
    OptionExpired,
    RepoRepaid,
    RepoForcedSale,
}

/// <summary>Algo del mercado que el juego debe comunicar (dividendo cobrado, vencimiento, licitación).</summary>
public sealed record MarketNotice(MarketNoticeKind Kind, EconomicDate Date, string Instrument, decimal Amount);
