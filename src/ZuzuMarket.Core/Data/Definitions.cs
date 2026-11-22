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

    /// <summary>Parámetros del modelo de precios.</summary>
    public CommodityMarketParameters Market { get; set; } = new();

    /// <summary>Factor estacional: más barato en cosecha, más caro fuera de estación.</summary>
    public double SeasonFactor(Season season)
    {
        if (AllYear)
        {
            return 1.0;
        }

        return HarvestSeasons.Contains(season) ? Market.InSeasonFactor : Market.OffSeasonFactor;
    }
}

/// <summary>
/// Parámetros del modelo de precios de un commodity (ver §6.2 del diseño). Todos por día.
/// </summary>
public sealed class CommodityMarketParameters
{
    /// <summary>Desvío estándar diario del ruido (0.03 = 3%).</summary>
    public double Volatility { get; set; } = 0.03;

    /// <summary>Fracción del desvío que se corrige por día (θ). 0.05 ≈ vida media de 14 días.</summary>
    public double MeanReversion { get; set; } = 0.05;

    /// <summary>Unidades que el mercado absorbe sin mover mucho el precio.</summary>
    public double BaseVolume { get; set; } = 300;

    /// <summary>Sensibilidad del precio al stock acumulado (α).</summary>
    public double Impact { get; set; } = 0.2;

    /// <summary>Fracción del stock acumulado que el mercado absorbe por día.</summary>
    public double StockDecay { get; set; } = 0.12;

    public double InSeasonFactor { get; set; } = 0.95;

    public double OffSeasonFactor { get; set; } = 1.30;
}

/// <summary>Para quién es bueno el evento: el granjero gana con precios altos de lo que vende.</summary>
public enum EventTone
{
    Positive,
    Negative,
    Neutral,
}

public enum Weather
{
    Sun,
    Rain,
    Storm,
    Snow,
    Wind,
}

public enum ImpactProfile
{
    /// <summary>El impacto se mantiene igual durante todo el evento.</summary>
    Constant,

    /// <summary>El impacto empieza completo y baja linealmente hasta el final del evento.</summary>
    Decay,
}

public sealed class EventEffect
{
    /// <summary>Id de commodity, <c>sector:&lt;Sector&gt;</c> o <c>all</c>.</summary>
    public string Target { get; set; } = "";

    /// <summary>Impacto logarítmico en el precio (0.2 ≈ +22%, −0.2 ≈ −18%).</summary>
    public double PriceImpact { get; set; }

    public ImpactProfile Profile { get; set; } = ImpactProfile.Constant;

    public bool Matches(CommodityDefinition commodity)
    {
        if (string.Equals(Target, "all", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Target.StartsWith(EventDefinition.SectorPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(Target.Substring(EventDefinition.SectorPrefix.Length), commodity.Sector, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(Target, commodity.Id, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class FixedEventDate
{
    public Season Season { get; set; }

    public int Day { get; set; }
}

/// <summary>Evento económico tal como figura en <c>events.json</c>. Los textos están en i18n (<c>event.&lt;id&gt;.*</c>).</summary>
public sealed class EventDefinition
{
    public const string SectorPrefix = "sector:";

    public string Id { get; set; } = "";

    /// <summary>Nombre de referencia (para logs; el juego usa i18n).</summary>
    public string Name { get; set; } = "";

    public EventTone Tone { get; set; } = EventTone.Neutral;

    /// <summary>Estaciones en que puede ocurrir (vacío = cualquiera).</summary>
    public List<Season> Seasons { get; set; } = new();

    /// <summary>Climas del día en que puede ocurrir (vacío = cualquiera).</summary>
    public List<Weather> Weather { get; set; } = new();

    /// <summary>Si se indica, el evento solo puede ocurrir ese día de cada año.</summary>
    public FixedEventDate? FixedDate { get; set; }

    public int MinYear { get; set; } = 1;

    /// <summary>Probabilidad de empezar en un día elegible (0 a 1).</summary>
    public double Probability { get; set; }

    public int DurationDays { get; set; } = 1;

    /// <summary>Días que tienen que pasar desde que termina para poder repetirse.</summary>
    public int CooldownDays { get; set; }

    public List<EventEffect> Effects { get; set; } = new();

    /// <summary>Se suma a la inflación del período en curso (0.01 = +1 punto).</summary>
    public double InflationShock { get; set; }

    /// <summary>Si además del aviso en pantalla manda una carta del Diario de Zuzu.</summary>
    public bool Letter { get; set; }
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
    private readonly Dictionary<string, CommodityDefinition> _commodityByItem;

    public GameData(
        ChartOfAccounts chart,
        IReadOnlyList<CommodityDefinition> commodities,
        IReadOnlyList<DifficultyDefinition> difficulties,
        IReadOnlyList<EventDefinition>? events = null)
    {
        Chart = chart;
        Commodities = commodities;
        Difficulties = difficulties;
        Events = events ?? Array.Empty<EventDefinition>();
        _commodityByItem = new Dictionary<string, CommodityDefinition>(StringComparer.Ordinal);
        foreach (CommodityDefinition commodity in commodities)
        {
            foreach (string itemId in commodity.ItemIds)
            {
                _commodityByItem[itemId] = commodity;
            }
        }
    }

    public IReadOnlyList<EventDefinition> Events { get; }

    public EventDefinition? FindEvent(string id) =>
        Events.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    public CommodityDefinition? FindCommodity(string id) =>
        Commodities.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    public ChartOfAccounts Chart { get; }

    public IReadOnlyList<CommodityDefinition> Commodities { get; }

    public IReadOnlyList<DifficultyDefinition> Difficulties { get; }

    public DifficultyDefinition? FindDifficulty(string id) =>
        Difficulties.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    public CommodityDefinition? FindCommodityByItemId(string itemId) =>
        _commodityByItem.TryGetValue(itemId, out CommodityDefinition? commodity) ? commodity : null;
}
