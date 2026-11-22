using ZuzuMarket.Core.Data;

namespace ZuzuMarket.Core.Markets;

/// <summary>Exposición de una empresa a uno o más commodities.</summary>
public sealed class Exposure
{
    /// <summary>Id de commodity, <c>sector:&lt;Sector&gt;</c> o <c>all</c>.</summary>
    public string Target { get; set; } = "";

    /// <summary>Cuánto se mueven los ingresos por cada 1 de variación del precio real del commodity.</summary>
    public double Weight { get; set; }

    public bool Matches(CommodityDefinition commodity) =>
        new EventEffect { Target = Target }.Matches(commodity);
}

/// <summary>Empresa que cotiza en BYMZ (<c>data/companies.json</c>).</summary>
public sealed class CompanyDefinition
{
    public string Ticker { get; set; } = "";

    public string Name { get; set; } = "";

    public string Sector { get; set; } = "";

    public long Shares { get; set; }

    /// <summary>Utilidad por acción anual de referencia, en oro del año 1.</summary>
    public double BaseEps { get; set; }

    /// <summary>Relación precio/utilidad del sector.</summary>
    public double Pe { get; set; }

    /// <summary>Fracción de la utilidad que se reparte como dividendo.</summary>
    public double Payout { get; set; }

    /// <summary>Desvío diario del sentimiento.</summary>
    public double Volatility { get; set; } = 0.02;

    public double MeanReversion { get; set; } = 0.03;

    /// <summary>Volumen diario típico (acciones), para el impacto de las órdenes grandes.</summary>
    public double AverageVolume { get; set; } = 10_000;

    public List<Exposure> Exposures { get; set; } = new();
}

public sealed class LetraTenor
{
    public int Days { get; set; }

    /// <summary>Prima sobre la tasa de referencia mensual (0.002 = 0,2 puntos).</summary>
    public double Premium { get; set; }
}

/// <summary>Reglas del mercado (<c>data/markets.json</c>).</summary>
public sealed class MarketRules
{
    public decimal CommissionRate { get; set; } = 0.005m;

    public decimal MinimumCommission { get; set; } = 1m;

    public decimal MarketFeeRate { get; set; } = 0.0008m;

    /// <summary>IVA sobre la comisión del ALyC.</summary>
    public decimal CommissionIvaRate { get; set; } = 0.21m;

    public double StockSpread { get; set; } = 0.005;

    public double LetraSpread { get; set; } = 0.002;

    /// <summary>Impacto en el precio por cada volumen diario completo operado.</summary>
    public double ImpactCoefficient { get; set; } = 0.1;

    public double MaxImpact { get; set; } = 0.10;

    /// <summary>Retención sobre dividendos (pago único y definitivo).</summary>
    public decimal DividendWithholding { get; set; } = 0.07m;

    /// <summary>Tasa real mensual que se suma a la inflación para la tasa de referencia.</summary>
    public double RealRate { get; set; } = 0.005;

    public double MinimumRate { get; set; } = 0.001;

    public List<LetraTenor> LetraTenors { get; set; } = new();

    public decimal LetraFaceValue { get; set; } = 100m;

    public List<int> CaucionTerms { get; set; } = new();

    /// <summary>Tasa de caución como fracción de la tasa de referencia.</summary>
    public double CaucionRateFactor { get; set; } = 0.85;

    public decimal CaucionMinimum { get; set; } = 100m;

    public double MerzuBase { get; set; } = 1000;

    /// <summary>Ticks intradiarios entre las 9:00 y las 17:00 (cada 10 minutos = 48).</summary>
    public int IntradayTicks { get; set; } = 48;
}
