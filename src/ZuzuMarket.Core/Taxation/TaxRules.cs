using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Taxation;

/// <summary>
/// Reglas tributarias tal como figuran en <c>data/taxes.json</c> (§9 del diseño).
/// Los montos fijos están en oro del año 1: se indexan por IPC al emitirse y se multiplican por la presión
/// tributaria de la dificultad (<see cref="DifficultyDefinition.TaxBurden"/>), igual que las alícuotas.
/// </summary>
public sealed class TaxRules
{
    /// <summary>Día de la estación en que vencen las boletas mensuales.</summary>
    public int DueDay { get; set; } = 14;

    /// <summary>Día de primavera en que vence la declaración anual de Ganancias.</summary>
    public int GananciasDueDay { get; set; } = 21;

    public Dictionary<string, string> ActivityBySector { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Actividad según la categoría del objeto del juego (<c>Object.Category</c>, p. ej. "-75" = verdura).</summary>
    public Dictionary<string, string> ActivityByCategory { get; set; } = new();

    public string DefaultActivity { get; set; } = "otros";

    public IvaRules Iva { get; set; } = new();

    public RateTable IngresosBrutos { get; set; } = new();

    public SeguridadHigieneRules SeguridadHigiene { get; set; } = new();

    public AblRules Abl { get; set; } = new();

    public MonotributoRules Monotributo { get; set; } = new();

    public GananciasRules Ganancias { get; set; } = new();

    public MoraRules Mora { get; set; } = new();

    /// <summary>Dónde se pagan las boletas a mano (lo usa el mod).</summary>
    public PaymentDesk PaymentDesk { get; set; } = new();

    /// <summary>Actividad tributaria de una venta: por sector del commodity, o por categoría del objeto.</summary>
    public string ActivityFor(CommodityDefinition? commodity, int itemCategory)
    {
        if (commodity is not null && ActivityBySector.TryGetValue(commodity.Sector, out string? bySector))
        {
            return bySector;
        }

        return ActivityByCategory.TryGetValue(itemCategory.ToString(System.Globalization.CultureInfo.InvariantCulture), out string? byCategory)
            ? byCategory
            : DefaultActivity;
    }
}

/// <summary>Alícuotas por actividad, con una alícuota por defecto.</summary>
public class RateTable
{
    public Dictionary<string, decimal> Rates { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public decimal Default { get; set; }

    public decimal For(string activity) => Rates.TryGetValue(activity, out decimal rate) ? rate : Default;
}

public sealed class IvaRules : RateTable
{
    /// <summary>Alícuota que se presume incluida en las compras en tiendas (crédito fiscal).</summary>
    public decimal PurchaseRate { get; set; } = 0.21m;
}

public sealed class SeguridadHigieneRules
{
    public decimal Rate { get; set; } = 0.006m;

    /// <summary>Mínimo por período en oro del año 1.</summary>
    public decimal Minimum { get; set; } = 100m;
}

public sealed class AblRules
{
    /// <summary>Tasa por la casa según su nivel de ampliación (0 a 3), por período.</summary>
    public List<decimal> HouseByLevel { get; set; } = new();

    /// <summary>Tasa por período por tipo de edificio (<c>Building.buildingType</c>).</summary>
    public Dictionary<string, decimal> Buildings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public decimal DefaultBuilding { get; set; }

    public decimal For(FarmSnapshot farm)
    {
        decimal house = HouseByLevel.Count == 0 ? 0 : HouseByLevel[Math.Clamp(farm.HouseLevel, 0, HouseByLevel.Count - 1)];
        return house + farm.Buildings.Sum(b => Buildings.TryGetValue(b, out decimal amount) ? amount : DefaultBuilding);
    }
}

public sealed class MonotributoCategory
{
    public string Id { get; set; } = "";

    /// <summary>Tope de facturación anual (últimas 4 estaciones) en oro del año 1.</summary>
    public decimal AnnualCap { get; set; }

    /// <summary>Cuota por estación en oro del año 1.</summary>
    public decimal Fee { get; set; }
}

public sealed class MonotributoRules
{
    public List<MonotributoCategory> Categories { get; set; } = new();

    /// <summary>Estaciones al final de las cuales se recategoriza (semestral).</summary>
    public List<Season> RecategorizationAfter { get; set; } = new();
}

public sealed class TaxBracket
{
    /// <summary>Límite superior del tramo en oro del año 1; <c>null</c> en el último tramo.</summary>
    public decimal? UpTo { get; set; }

    public decimal Rate { get; set; }
}

public sealed class GananciasRules
{
    /// <summary>Mínimo no imponible anual en oro del año 1.</summary>
    public decimal NonTaxableMinimum { get; set; }

    public List<TaxBracket> Brackets { get; set; } = new();

    /// <summary>Cantidad de anticipos del año siguiente.</summary>
    public int Advances { get; set; } = 4;

    /// <summary>Fracción del impuesto determinado que representa cada anticipo.</summary>
    public decimal AdvanceFraction { get; set; } = 0.25m;

    /// <summary>Impuesto según la escala progresiva; los límites de los tramos se escalan por <paramref name="scale"/> (IPC).</summary>
    public decimal ProgressiveTax(decimal taxable, decimal scale)
    {
        decimal tax = 0;
        decimal lower = 0;
        foreach (TaxBracket bracket in Brackets)
        {
            if (taxable <= lower)
            {
                break;
            }

            decimal upper = bracket.UpTo is decimal upTo ? upTo * scale : decimal.MaxValue;
            decimal portion = Math.Min(taxable, upper) - lower;
            tax += portion * bracket.Rate;
            lower = upper;
        }

        return tax;
    }
}

public sealed class MoraRules
{
    /// <summary>Interés resarcitorio diario sobre el capital (0.001 = 0,1%).</summary>
    public decimal DailyInterest { get; set; } = 0.001m;

    /// <summary>Días de atraso hasta la intimación.</summary>
    public int IntimationAfterDays { get; set; } = 7;

    /// <summary>Plazo que da la intimación.</summary>
    public int IntimationDeadlineDays { get; set; } = 7;

    /// <summary>Multa cuando vence la intimación, sobre el capital.</summary>
    public decimal FineRate { get; set; } = 0.10m;
}

public sealed class PaymentDesk
{
    public string Location { get; set; } = "ManorHouse";

    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; } = 1;

    public int Height { get; set; } = 1;

    public bool Contains(string location, int x, int y) =>
        string.Equals(location, Location, StringComparison.OrdinalIgnoreCase)
        && x >= X && x < X + Width && y >= Y && y < Y + Height;
}

/// <summary>Lo que el municipio necesita saber de la granja para cobrar el ABL.</summary>
public sealed record FarmSnapshot(int HouseLevel, IReadOnlyList<string> Buildings)
{
    public static FarmSnapshot Empty { get; } = new(0, Array.Empty<string>());
}
