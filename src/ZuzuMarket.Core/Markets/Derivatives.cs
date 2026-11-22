using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Markets;

/// <summary>Parámetros de futuros y opciones (<c>markets.json</c> → <c>derivatives</c>).</summary>
public sealed class DerivativesRules
{
    public int FuturesMaturities { get; set; } = 3;

    public double FuturesSpread { get; set; } = 0.002;

    public decimal FuturesCommissionRate { get; set; } = 0.001m;

    /// <summary>Garantía inicial como fracción del nocional (se multiplica por la volatilidad de la dificultad).</summary>
    public double InitialMargin { get; set; } = 0.15;

    /// <summary>Garantía mínima como fracción de la inicial.</summary>
    public double MaintenanceRatio { get; set; } = 0.75;

    /// <summary>Multa por cada unidad no entregada, como fracción del precio de liquidación.</summary>
    public double DeliveryPenalty { get; set; } = 0.20;

    /// <summary>Prima por calidad en la entrega: plata, oro, iridio.</summary>
    public double SilverPremium { get; set; } = 0.10;

    public double GoldPremium { get; set; } = 0.20;

    public double IridiumPremium { get; set; } = 0.40;

    public int OptionLot { get; set; } = 10;

    public int OptionMaturities { get; set; } = 2;

    public List<double> OptionStrikes { get; set; } = new() { 0.90, 0.95, 1.00, 1.05, 1.10 };

    public double OptionSpread { get; set; } = 0.03;

    public decimal OptionCommissionRate { get; set; } = 0.005m;

    /// <summary>Garantía de una opción lanzada descubierta: prima + esta fracción del subyacente − monto fuera del dinero.</summary>
    public double ShortOptionMarginRate { get; set; } = 0.20;

    /// <summary>Mínimo de esa garantía como fracción del subyacente.</summary>
    public double ShortOptionMinimumRate { get; set; } = 0.10;

    public double QualityPremium(int quality) => quality switch
    {
        1 => SilverPremium,
        2 => GoldPremium,
        >= 4 => IridiumPremium,
        _ => 0,
    };
}

/// <summary>Contrato de futuro listado en MATZU.</summary>
public sealed class FuturesContract
{
    public string Id { get; set; } = "";

    public string CommodityId { get; set; } = "";

    public FiscalPeriod Period { get; set; }

    /// <summary>Último día hábil de la estación: liquidación y entrega.</summary>
    public EconomicDate Expiry { get; set; }

    /// <summary>Precio de ajuste del último día hábil (para la variación diaria).</summary>
    public double LastSettlement { get; set; }

    public double PreviousSettlement { get; set; }
}

/// <summary>Posición neta en un contrato: positiva = comprado, negativa = vendido.</summary>
public sealed class FuturesPosition
{
    public string ContractId { get; set; } = "";

    public long Quantity { get; set; }

    /// <summary>Precio promedio de entrada (para mostrar el resultado acumulado).</summary>
    public double EntryPrice { get; set; }

    /// <summary>Precio contra el que se hizo la última diferencia diaria.</summary>
    public double LastSettle { get; set; }
}

public enum OptionType
{
    Call,
    Put,
}

/// <summary>Serie de opciones europeas sobre una acción (lote = <see cref="DerivativesRules.OptionLot"/> acciones).</summary>
public sealed class OptionSeries
{
    public string Id { get; set; } = "";

    public string Ticker { get; set; } = "";

    public OptionType Type { get; set; }

    public double Strike { get; set; }

    public FiscalPeriod Period { get; set; }

    public EconomicDate Expiry { get; set; }
}

/// <summary>Posición neta en una serie: positiva = comprada, negativa = lanzada.</summary>
public sealed class OptionPosition
{
    public string SeriesId { get; set; } = "";

    public long Quantity { get; set; }

    /// <summary>Prima total pagada (comprada) o cobrada (lanzada), igual al saldo en el libro.</summary>
    public decimal PremiumBasis { get; set; }
}

/// <summary>Mercadería en el Silo de Acopio (objeto del juego, calidad, cantidad y precio base vanilla por unidad).</summary>
public sealed class SiloItem
{
    public string ItemId { get; set; } = "";

    public int Quality { get; set; }

    public int Stack { get; set; }

    public int BasePrice { get; set; }
}

public sealed class DerivativesState
{
    public List<FuturesContract> Futures { get; set; } = new();

    public List<FuturesPosition> FuturesPositions { get; set; } = new();

    public List<OptionSeries> Options { get; set; } = new();

    public List<OptionPosition> OptionPositions { get; set; } = new();

    public List<SiloItem> Silo { get; set; } = new();

    /// <summary>Si hay una llamada de margen abierta: hasta qué día hábil hay para reponer.</summary>
    public EconomicDate? MarginCallDeadline { get; set; }

    public FiscalPeriod? ListedFor { get; set; }
}

/// <summary>Cotización de una operación con derivados.</summary>
public sealed record DerivativeQuote(
    InstrumentKind Kind,
    string Instrument,
    TradeSide Side,
    long Quantity,
    double UnitPrice,
    decimal Notional,
    decimal Commission,
    decimal Iva,
    decimal MarginAfter,
    TradeError Error)
{
    public bool IsValid => Error == TradeError.None;
}

/// <summary>Black-Scholes para opciones europeas.</summary>
public static class BlackScholes
{
    public static double Price(OptionType type, double spot, double strike, double years, double rate, double volatility)
    {
        if (years <= 0 || volatility <= 0)
        {
            return Math.Max(0, type == OptionType.Call ? spot - strike : strike - spot);
        }

        (double d1, double d2) = D(spot, strike, years, rate, volatility);
        double discount = Math.Exp(-rate * years);
        return type == OptionType.Call
            ? (spot * Cdf(d1)) - (strike * discount * Cdf(d2))
            : (strike * discount * Cdf(-d2)) - (spot * Cdf(-d1));
    }

    public static double Delta(OptionType type, double spot, double strike, double years, double rate, double volatility)
    {
        if (years <= 0 || volatility <= 0)
        {
            bool itm = type == OptionType.Call ? spot > strike : strike > spot;
            return itm ? (type == OptionType.Call ? 1 : -1) : 0;
        }

        double d1 = D(spot, strike, years, rate, volatility).D1;
        return type == OptionType.Call ? Cdf(d1) : Cdf(d1) - 1;
    }

    private static (double D1, double D2) D(double spot, double strike, double years, double rate, double volatility)
    {
        double sqrt = volatility * Math.Sqrt(years);
        double d1 = (Math.Log(spot / strike) + ((rate + (volatility * volatility / 2)) * years)) / sqrt;
        return (d1, d1 - sqrt);
    }

    /// <summary>Normal estándar acumulada (aproximación de Abramowitz-Stegun, error &lt; 1e-7).</summary>
    public static double Cdf(double x)
    {
        double t = 1 / (1 + (0.2316419 * Math.Abs(x)));
        double poly = t * (0.319381530 + (t * (-0.356563782 + (t * (1.781477937 + (t * (-1.821255978 + (t * 1.330274429))))))));
        double density = Math.Exp(-x * x / 2) / Math.Sqrt(2 * Math.PI);
        double upper = density * poly;
        return x >= 0 ? 1 - upper : upper;
    }
}
