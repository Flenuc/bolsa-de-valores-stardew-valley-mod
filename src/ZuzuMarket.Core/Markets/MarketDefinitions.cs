using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Macro;

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

/// <summary>Punto de la prima por plazo de la curva.</summary>
public sealed class TermPoint
{
    public int Days { get; set; }

    public double Premium { get; set; }
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

    public CentralBankRules CentralBank { get; set; } = new();

    /// <summary>Prima por plazo (mensual) según los días al vencimiento; se interpola linealmente.</summary>
    public List<TermPoint> TermPremium { get; set; } = new();

    public double BondSpread { get; set; } = 0.003;

    /// <summary>Bono que se entrega en el canje: cupón fijo por estación, plazo y cuotas de amortización.</summary>
    public double RestructuredCoupon { get; set; } = 0.005;

    public int RestructuredTenor { get; set; } = 12;

    public int RestructuredInstallments { get; set; } = 4;

    public double Premium(int days)
    {
        if (TermPremium.Count == 0)
        {
            return 0;
        }

        List<TermPoint> points = TermPremium.OrderBy(p => p.Days).ToList();
        if (days <= points[0].Days)
        {
            return points[0].Premium;
        }

        for (int i = 1; i < points.Count; i++)
        {
            if (days <= points[i].Days)
            {
                double t = (days - points[i - 1].Days) / (double)(points[i].Days - points[i - 1].Days);
                return points[i - 1].Premium + (t * (points[i].Premium - points[i - 1].Premium));
            }
        }

        return points[^1].Premium;
    }
}
