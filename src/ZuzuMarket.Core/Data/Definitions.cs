using ZuzuMarket.Core.Ledger;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Data;

/// <summary>Cuenta del plan de cuentas tal como figura en <c>accounts.json</c>.</summary>
public sealed class AccountDefinition
{
    public string Code { get; set; } = "";

    public string Name { get; set; } = "";

    public AccountType Type { get; set; }
}

public enum CommodityPriceMode
{
    /// <summary>El precio del commodity se aplica a los objetos que lo componen (con el multiplicador de calidad vanilla).</summary>
    Item,

    /// <summary>El commodity es un índice que multiplica el precio vanilla de cada variante (vino, canasta de pescado).</summary>
    Index,
}

/// <summary>Commodity tal como figura en <c>commodities.json</c>.</summary>
public sealed class CommodityDefinition
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Sector { get; set; } = "";

    /// <summary>IDs de objetos del juego (Stardew Valley 1.6) que pertenecen al commodity.</summary>
    public List<string> ItemIds { get; set; } = new();

    public CommodityPriceMode PriceMode { get; set; } = CommodityPriceMode.Item;

    /// <summary>Estaciones de cosecha. Vacío si <see cref="AllYear"/> es verdadero.</summary>
    public List<Season> HarvestSeasons { get; set; } = new();

    public bool AllYear { get; set; }

    /// <summary>Unidades por contrato de futuro en MATZU.</summary>
    public int FuturesContractSize { get; set; }
}

/// <summary>Parámetros de un nivel de dificultad (§12 del diseño).</summary>
public sealed class DifficultyDefinition
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Inflación por estación (período mensual), mínimo y máximo, como fracción (0.02 = 2%).</summary>
    public decimal InflationMin { get; set; }

    public decimal InflationMax { get; set; }

    /// <summary>Multiplicador de la volatilidad de precios.</summary>
    public decimal PriceVolatility { get; set; } = 1m;

    /// <summary>Multiplicador de la frecuencia de eventos negativos.</summary>
    public decimal NegativeEventFrequency { get; set; } = 1m;

    /// <summary>Multiplicador de las alícuotas de referencia.</summary>
    public decimal TaxBurden { get; set; } = 1m;

    /// <summary>Multiplicador de retenciones y aranceles.</summary>
    public decimal TradeDuties { get; set; } = 1m;

    /// <summary>Multiplicador del interés por mora.</summary>
    public decimal LateInterest { get; set; } = 1m;

    /// <summary>Tipos de embargo habilitados: "ingresos", "cuentas", "bienes", "edificios".</summary>
    public List<string> Embargoes { get; set; } = new();

    public bool BankruptcyEnabled { get; set; }

    /// <summary>Multiplicador de la velocidad con que avanza el proceso de quiebra.</summary>
    public decimal BankruptcySpeed { get; set; } = 1m;

    /// <summary>Probabilidad anual de default soberano.</summary>
    public decimal SovereignDefaultProbability { get; set; }

    public bool CurrencyControls { get; set; }

    /// <summary>Multiplicador de la frecuencia de moratorias y blanqueos.</summary>
    public decimal AmnestyFrequency { get; set; } = 1m;
}

/// <summary>Todos los datos del mod ya validados.</summary>
public sealed class GameData
{
    public GameData(
        ChartOfAccounts chart,
        IReadOnlyList<CommodityDefinition> commodities,
        IReadOnlyList<DifficultyDefinition> difficulties)
    {
        Chart = chart;
        Commodities = commodities;
        Difficulties = difficulties;
    }

    public ChartOfAccounts Chart { get; }

    public IReadOnlyList<CommodityDefinition> Commodities { get; }

    public IReadOnlyList<DifficultyDefinition> Difficulties { get; }

    public DifficultyDefinition? FindDifficulty(string id) =>
        Difficulties.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    public CommodityDefinition? FindCommodityByItemId(string itemId) =>
        Commodities.FirstOrDefault(c => c.ItemIds.Contains(itemId));
}
