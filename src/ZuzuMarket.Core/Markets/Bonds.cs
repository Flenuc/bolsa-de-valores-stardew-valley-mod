using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Markets;

public enum BondType
{
    /// <summary>Cupón fijo, fijado al emitir según la curva.</summary>
    Fixed,

    /// <summary>Cupón = tasa de política vigente + margen.</summary>
    Floating,

    /// <summary>Capital ajustado por CER-F (IPC) con un cupón real.</summary>
    Cer,
}

/// <summary>Plantilla de emisión (<c>data/bonds.json</c>): se emite una serie al arrancar y cada primavera.</summary>
public sealed class BondTemplate
{
    public string Prefix { get; set; } = "";

    /// <summary><c>FERNGILL</c> (soberano) o el ticker de una empresa (obligación negociable).</summary>
    public string Issuer { get; set; } = "";

    public BondType Type { get; set; }

    public int TenorSeasons { get; set; }

    /// <summary>Variable: margen sobre la tasa de política. CER: cupón real. Fijo: se ignora (se fija con la curva).</summary>
    public double Coupon { get; set; }

    /// <summary>Cuotas de amortización al final (1 = todo al vencimiento).</summary>
    public int AmortizationInstallments { get; set; } = 1;
}

/// <summary>Serie emitida.</summary>
public sealed class BondSeries
{
    public string Id { get; set; } = "";

    public string Prefix { get; set; } = "";

    public string Issuer { get; set; } = "";

    public BondType Type { get; set; }

    public EconomicDate IssueDate { get; set; }

    public int TenorSeasons { get; set; }

    /// <summary>Tasa por estación: cupón fijo, margen (variable) o cupón real (CER).</summary>
    public double CouponRate { get; set; }

    public int AmortizationInstallments { get; set; } = 1;

    /// <summary>IPC el día de la emisión (base del CER-F).</summary>
    public double CerBase { get; set; } = 1;

    public int PaymentsMade { get; set; }

    /// <summary>Fracción del valor nominal que falta amortizar.</summary>
    public double Outstanding { get; set; } = 1;

    public bool IsSovereign => Issuer == CreditEngine.Sovereign;

    public bool IsRestructured { get; set; }

    public EconomicDate PaymentDate(int payment) => IssueDate.FiscalPeriod.AddSeasons(payment).FirstDay;

    public EconomicDate Maturity => PaymentDate(TenorSeasons);

    public EconomicDate? NextPayment => PaymentsMade < TenorSeasons ? PaymentDate(PaymentsMade + 1) : null;

    /// <summary>Fracción del nominal que se amortiza en el pago <paramref name="payment"/>.</summary>
    public double AmortizationFraction(int payment) =>
        payment > TenorSeasons - AmortizationInstallments && payment <= TenorSeasons ? 1.0 / AmortizationInstallments : 0;

    /// <summary>Nominal pendiente antes del pago <paramref name="payment"/>.</summary>
    public double OutstandingBefore(int payment)
    {
        double outstanding = 1;
        for (int k = 1; k < payment; k++)
        {
            outstanding -= AmortizationFraction(k);
        }

        return Math.Max(0, outstanding);
    }
}

/// <summary>Flujo de un pago por lámina de VN 100.</summary>
public sealed record BondCashFlow(int Payment, EconomicDate Date, double Coupon, double Amortization)
{
    public double Total => Coupon + Amortization;
}

/// <summary>Variables de mercado para valuar un bono.</summary>
public sealed record BondContext(double PolicyRate, double InflationRate, double InflationIndex, Func<int, double> NominalYield);

/// <summary>Valuación de bonos: flujos, precio, TIR y duración (todo por lámina de VN 100).</summary>
public static class BondMath
{
    public const double Face = 100;

    /// <summary>Flujos que faltan cobrar, proyectados con las tasas e índices de hoy.</summary>
    public static List<BondCashFlow> RemainingFlows(BondSeries s, BondContext ctx)
    {
        double cer = s.Type == BondType.Cer ? ctx.InflationIndex / s.CerBase : 1;
        double rate = s.Type switch
        {
            BondType.Floating => ctx.PolicyRate + s.CouponRate,
            _ => s.CouponRate,
        };

        var flows = new List<BondCashFlow>();
        for (int k = s.PaymentsMade + 1; k <= s.TenorSeasons; k++)
        {
            double outstanding = s.OutstandingBefore(k);
            flows.Add(new BondCashFlow(k, s.PaymentDate(k), Face * outstanding * rate * cer, Face * s.AmortizationFraction(k) * cer));
        }

        return flows;
    }

    /// <summary>Tasa mensual a la que se descuentan los flujos (real para los bonos CER).</summary>
    public static double DiscountRate(BondSeries s, BondContext ctx, int days) =>
        s.Type == BondType.Cer
            ? Math.Max(0.0005, ctx.NominalYield(days) - ctx.InflationRate)
            : ctx.NominalYield(days);

    /// <summary>Valor presente de los flujos (sin default).</summary>
    public static double Price(BondSeries s, EconomicDate date, BondContext ctx) =>
        RemainingFlows(s, ctx).Sum(f =>
        {
            int days = Math.Max(0, date.DaysUntil(f.Date));
            return f.Total / Math.Pow(1 + DiscountRate(s, ctx, days), days / (double)EconomicDate.DaysPerSeason);
        });

    /// <summary>Precio en default: el valor de recupero del nominal pendiente.</summary>
    public static double DefaultedPrice(BondSeries s, BondContext ctx, double recovery) =>
        Face * s.Outstanding * recovery * (s.Type == BondType.Cer ? ctx.InflationIndex / s.CerBase : 1);

    /// <summary>TIR mensual: la tasa única que iguala los flujos al precio (bisección).</summary>
    public static double Yield(BondSeries s, EconomicDate date, BondContext ctx, double price)
    {
        List<BondCashFlow> flows = RemainingFlows(s, ctx);
        if (flows.Count == 0 || price <= 0)
        {
            return 0;
        }

        double Pv(double y) => flows.Sum(f => f.Total / Math.Pow(1 + y, Math.Max(0, date.DaysUntil(f.Date)) / (double)EconomicDate.DaysPerSeason));
        double low = -0.5;
        double high = 2.0;
        for (int i = 0; i < 100; i++)
        {
            double mid = (low + high) / 2;
            if (Pv(mid) > price)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return (low + high) / 2;
    }

    /// <summary>Duración de Macaulay en meses (estaciones).</summary>
    public static double Duration(BondSeries s, EconomicDate date, BondContext ctx)
    {
        double weighted = 0;
        double total = 0;
        foreach (BondCashFlow f in RemainingFlows(s, ctx))
        {
            double t = Math.Max(0, date.DaysUntil(f.Date)) / (double)EconomicDate.DaysPerSeason;
            double pv = f.Total / Math.Pow(1 + DiscountRate(s, ctx, date.DaysUntil(f.Date)), t);
            weighted += t * pv;
            total += pv;
        }

        return total > 0 ? weighted / total : 0;
    }
}
