using ZuzuMarket.Core.Markets;
using ZuzuMarket.Core.Taxation;
using ZuzuMarket.Core.Time;
using ZuzuMarket.Core.Trade;

namespace ZuzuMarket.Core.Companies;

/// <summary>Tipo de sociedad que puede crear el jugador.</summary>
public sealed class CompanyTypeDefinition
{
    public string Id { get; set; } = "";

    /// <summary>Actividades (las de <c>taxes.json</c>) cuyas ventas absorbe.</summary>
    public List<string> Activities { get; set; } = new();

    /// <summary>Requisito de desbloqueo (lo evalúa el juego): none, mine40, fishing6, machines5, year2.</summary>
    public string Unlock { get; set; } = "none";

    /// <summary>Bonificación del precio de venta de su rubro (venta directa, sin acopiador).</summary>
    public decimal PriceBonus { get; set; }

    /// <summary>Habilita el contenedor de exportación a Gotoro.</summary>
    public bool Exports { get; set; }
}

/// <summary>Forma societaria.</summary>
public sealed class LegalFormDefinition
{
    public string Id { get; set; } = "";

    /// <summary>Capital mínimo en oro del año 1 (se indexa por IPC).</summary>
    public decimal MinimumCapital { get; set; }

    /// <summary>Tasa de inscripción en la Inspección de Personas Jurídicas (oro del año 1, indexada).</summary>
    public decimal RegistrationFee { get; set; }

    /// <summary>Puede emitir ON y abrir su capital (Fase 7B).</summary>
    public bool CanListOnMarket { get; set; }
}

public sealed class PlayerCompanyRules
{
    public List<CompanyTypeDefinition> Types { get; set; } = new();

    public List<LegalFormDefinition> Forms { get; set; } = new();

    /// <summary>Costo de transformar una S.R.L. en S.A. (además de completar el capital mínimo).</summary>
    public decimal TransformationFee { get; set; } = 1000m;

    /// <summary>Tasa de fiscalización por estación (oro del año 1, indexada).</summary>
    public decimal SeasonalFee { get; set; } = 100m;

    /// <summary>Días con deudas en gestión de cobro hasta la quiebra de la sociedad (÷ velocidad de la dificultad).</summary>
    public int InsolvencyAfterDays { get; set; } = 28;

    public CompanyTypeDefinition? FindType(string id) => Types.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public LegalFormDefinition? FindForm(string id) => Forms.FirstOrDefault(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Una sociedad del jugador.</summary>
public sealed class CompanyState
{
    /// <summary>Entidad del libro ("emp1"…).</summary>
    public string Id { get; set; } = "";

    public string Type { get; set; } = "";

    public string Form { get; set; } = "";

    public string Cuit { get; set; } = "";

    public EconomicDate CreatedOn { get; set; }

    public bool Active { get; set; } = true;

    public EconomicDate? DissolvedOn { get; set; }

    /// <summary>Quebró (sociedad disuelta por insolvencia).</summary>
    public bool Bankrupt { get; set; }

    public TaxState Taxes { get; set; } = new();

    public EconomicDate? CollectionSince { get; set; }

    public string Rating { get; set; } = "BB";

    public double Score { get; set; } = 50;

    public decimal DividendsPaid { get; set; }

    /// <summary>Capital aportado por el jugador (costo de su participación).</summary>
    public decimal Contributed { get; set; }
}

public sealed class CompaniesState
{
    public List<CompanyState> Companies { get; set; } = new();

    public int NextCompany { get; set; } = 1;

    /// <summary>Debug: desbloquea todos los tipos.</summary>
    public bool UnlockAll { get; set; }

    /// <summary>Contenedor de exportación (cofre virtual).</summary>
    public List<SiloItem> ExportContainer { get; set; } = new();

    public List<ExportReceivable> Receivables { get; set; } = new();

    public long NextReceivable { get; set; } = 1;
}

public enum CompanyNoticeKind
{
    Created,
    Transformed,
    Dissolved,
    Bankrupt,
    Exported,
    ExportCollected,
    TaxesOverdue,
    InCollection,
}

public sealed record CompanyNotice(CompanyNoticeKind Kind, EconomicDate Date, string Company, decimal Amount = 0, string? Detail = null);

public enum CompanyError
{
    None,
    UnknownType,
    UnknownForm,
    Locked,
    AlreadyExists,
    InsufficientFunds,
    BelowMinimumCapital,
    Inhibited,
    NotFound,
    NotAllowed,
    NothingToDistribute,
}
