using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Taxation;

public enum TaxRegime
{
    Monotributo,
    ResponsableInscripto,
}

public enum Jurisdiction
{
    /// <summary>Nación: Agencia de Recaudación de Ferngill.</summary>
    Arf,

    /// <summary>Provincia: Agencia de Rentas del Valle.</summary>
    Arpv,

    /// <summary>Municipalidad de Pelican Town.</summary>
    Municipalidad,
}

public enum TaxKind
{
    Monotributo,
    Iva,
    Ganancias,
    GananciasAnticipo,
    IngresosBrutos,
    SeguridadHigiene,
    Abl,
}

public enum ObligationStatus
{
    /// <summary>Emitida, todavía no venció.</summary>
    Pending,

    /// <summary>Vencida: devenga intereses resarcitorios.</summary>
    Overdue,

    /// <summary>Intimada por carta documento, con un plazo final.</summary>
    Intimated,

    /// <summary>Venció la intimación: se aplicó la multa y queda en gestión de cobro (embargos en la Fase 6).</summary>
    InCollection,

    Paid,
}

public static class TaxKindExtensions
{
    public static Jurisdiction Jurisdiction(this TaxKind kind) => kind switch
    {
        TaxKind.IngresosBrutos => Taxation.Jurisdiction.Arpv,
        TaxKind.SeguridadHigiene or TaxKind.Abl => Taxation.Jurisdiction.Municipalidad,
        _ => Taxation.Jurisdiction.Arf,
    };
}

/// <summary>Boleta de un impuesto.</summary>
public sealed class TaxObligation
{
    public long Id { get; set; }

    public TaxKind Kind { get; set; }

    /// <summary>Período al que corresponde (para Ganancias, el invierno del año declarado).</summary>
    public FiscalPeriod Period { get; set; }

    /// <summary>Capital en oro.</summary>
    public long Principal { get; set; }

    /// <summary>Intereses resarcitorios devengados (se asientan al pagar).</summary>
    public decimal Interest { get; set; }

    /// <summary>Multa aplicada al vencer la intimación (ya asentada).</summary>
    public long Fine { get; set; }

    public EconomicDate IssuedOn { get; set; }

    public EconomicDate DueDate { get; set; }

    public EconomicDate? IntimationDeadline { get; set; }

    public EconomicDate? LastAccrual { get; set; }

    public ObligationStatus Status { get; set; } = ObligationStatus.Pending;

    public EconomicDate? PaidOn { get; set; }

    public long PaidAmount { get; set; }

    public Jurisdiction Jurisdiction => Kind.Jurisdiction();

    public bool IsPaid => Status == ObligationStatus.Paid;

    /// <summary>Total a pagar hoy: capital + intereses (redondeados) + multa.</summary>
    public long Total => Principal + Money.MoneyRounding.ToGold(Interest) + Fine;
}

/// <summary>Acumulados de un período fiscal (estación) para las declaraciones mensuales.</summary>
public sealed class PeriodAccumulator
{
    public FiscalPeriod Period { get; set; }

    public TaxRegime Regime { get; set; }

    /// <summary>Ventas netas de IVA por actividad.</summary>
    public Dictionary<string, decimal> NetSalesByActivity { get; set; } = new();

    public decimal NetPurchases { get; set; }

    public decimal NetSales => NetSalesByActivity.Values.Sum();
}

/// <summary>Acumulados del ejercicio para Ganancias (solo lo que ocurrió como Responsable Inscripto).</summary>
public sealed class YearAccumulator
{
    public int Year { get; set; }

    public bool HadResponsableInscripto { get; set; }

    public decimal NetSales { get; set; }

    public decimal NetPurchases { get; set; }

    /// <summary>IIBB, Seguridad e Higiene y ABL emitidos en el año (deducibles).</summary>
    public decimal DeductibleTaxes { get; set; }
}

public sealed record PeriodRevenue(FiscalPeriod Period, decimal NetSales);

/// <summary>Estado tributario serializable del jugador.</summary>
public sealed class TaxState
{
    /// <summary>Si ya se dio de alta (se hace el primer día que el mod simula la partida).</summary>
    public bool Started { get; set; }

    public EconomicDate? RegisteredOn { get; set; }

    public TaxRegime Regime { get; set; } = TaxRegime.Monotributo;

    /// <summary>Categoría del Monotributo (A…K).</summary>
    public string Category { get; set; } = "";

    public PeriodAccumulator Current { get; set; } = new();

    public YearAccumulator Year { get; set; } = new();

    /// <summary>Facturación neta de cada período cerrado (para recategorizar).</summary>
    public List<PeriodRevenue> RevenueHistory { get; set; } = new();

    /// <summary>Anticipos de Ganancias que faltan emitir y su monto.</summary>
    public int AdvancesRemaining { get; set; }

    public decimal AdvanceAmount { get; set; }

    public long NextObligationId { get; set; } = 1;

    public List<TaxObligation> Obligations { get; set; } = new();
}

public enum TaxNoticeKind
{
    /// <summary>Alta como monotributista.</summary>
    Registered,

    /// <summary>Se emitió una boleta.</summary>
    Issued,

    Overdue,

    Intimated,

    InCollection,

    Paid,

    /// <summary>Cambio de categoría del Monotributo (<see cref="TaxNotice.Detail"/> = categoría nueva).</summary>
    Recategorized,

    /// <summary>Exclusión del Monotributo: pasa a Responsable Inscripto.</summary>
    Excluded,

    /// <summary>Cambio de régimen manual (debug).</summary>
    RegimeChanged,
}

/// <summary>Algo que el juego debe comunicar al jugador (carta, aviso).</summary>
public sealed record TaxNotice(TaxNoticeKind Kind, EconomicDate Date, long? ObligationId = null, string? Detail = null);
