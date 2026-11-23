using ZuzuMarket.Core.Random;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Macro;

/// <summary>Parámetros del tipo de cambio de una dificultad (data/fx.json).</summary>
public sealed class FxDifficultyRules
{
    /// <summary>Fracción de la inflación diaria que traslada el oficial (crawling peg).</summary>
    public double Crawl { get; set; } = 1.0;

    /// <summary>Ruido diario del oficial.</summary>
    public double Volatility { get; set; } = 0.002;

    /// <summary>Probabilidad de devaluación por estación y rango del salto.</summary>
    public double DevaluationProbability { get; set; }

    public double DevaluationMin { get; set; } = 0.2;

    public double DevaluationMax { get; set; } = 0.6;

    /// <summary>Traslado de la devaluación a la inflación mensual (shock = traslado × salto).</summary>
    public double PassThrough { get; set; } = 0.15;

    /// <summary>Cepo: cupo y recargo en el oficial, brecha con el MEP y el blue.</summary>
    public bool Cepo { get; set; }

    /// <summary>Cupo de compra de oficial por estación, en oro gotoro.</summary>
    public decimal Quota { get; set; } = 200;

    /// <summary>Recargo sobre el oficial al comprar (impuesto a la compra de divisas).</summary>
    public decimal Surcharge { get; set; }

    /// <summary>Diferencia entre compra y venta del oficial.</summary>
    public decimal Spread { get; set; } = 0.01m;

    /// <summary>Brecha media: base + peso × inflación mensual.</summary>
    public double GapBase { get; set; }

    public double GapInflationWeight { get; set; }

    public double GapReversion { get; set; } = 0.05;

    public double GapVolatility { get; set; }

    /// <summary>El blue cotiza por encima del MEP, con puntas abiertas.</summary>
    public double BlueExtra { get; set; } = 0.05;

    public decimal BlueSpread { get; set; } = 0.04m;

    /// <summary>Días hábiles de parking entre las dos patas del MEP.</summary>
    public int ParkingDays { get; set; } = 1;

    /// <summary>Parte de la exportación que se liquida al MEP (dólar blend); el resto, al oficial.</summary>
    public double ExportBlend { get; set; } = 0.2;

    /// <summary>Recargo de las importaciones sobre el oficial.</summary>
    public double ImportSurcharge { get; set; } = 0.3;

    /// <summary>Probabilidad de un operativo el día después de comprar blue, y multa sobre lo comprado.</summary>
    public double RaidChance { get; set; } = 0.05;

    public decimal RaidFine { get; set; } = 0.5m;
}

public sealed class FxRules
{
    /// <summary>Oro local por 1 oro gotoro el primer día.</summary>
    public double InitialRate { get; set; } = 50;

    /// <summary>Tasa de Gotoro por estación (base de los bonos en oro gotoro).</summary>
    public double GotoroRate { get; set; } = 0.008;

    public Dictionary<string, FxDifficultyRules> Difficulties { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public FxDifficultyRules For(string difficulty) => Difficulties.TryGetValue(difficulty, out FxDifficultyRules? r) ? r : new FxDifficultyRules();
}

public sealed record FxClose(EconomicDate Date, double Official, double Mep, double Blue);

/// <summary>Estado serializable del tipo de cambio.</summary>
public sealed class FxState
{
    public bool Started { get; set; }

    public double Official { get; set; }

    /// <summary>Brecha del MEP sobre el oficial (0,8 = 80%).</summary>
    public double Gap { get; set; }

    public List<FxClose> History { get; set; } = new();

    public List<FxDevaluation> Devaluations { get; set; } = new();
}

public sealed record FxDevaluation(EconomicDate Date, double Size, double Before, double After);

/// <summary>
/// Tipo de cambio con el oro gotoro (§5.3): oficial con crawling peg y devaluaciones; con cepo, MEP y blue con brecha.
/// </summary>
public sealed class ExchangeRateModel
{
    public const string Stream = "cambio";

    private readonly FxRules _rules;
    private readonly FxState _s;

    public ExchangeRateModel(FxRules rules, FxState state)
    {
        _rules = rules;
        _s = state;
    }

    public FxState State => _s;

    public FxRules Rules => _rules;

    public double Official => _s.Official;

    public double Gap => _s.Gap;

    public double Mep => _s.Official * (1 + _s.Gap);

    /// <summary>Blue (punta media): un poco por encima del MEP cuando hay cepo.</summary>
    public double Blue(FxDifficultyRules r) => r.Cepo ? Mep * (1 + r.BlueExtra) : _s.Official;

    public void Start(EconomicDate date, FxDifficultyRules r, double monthlyInflation)
    {
        _s.Official = _rules.InitialRate;
        _s.Gap = r.Cepo ? TargetGap(r, monthlyInflation) : 0;
        _s.History.Add(new FxClose(date, _s.Official, Mep, Blue(r)));
        _s.Started = true;
    }

    private static double TargetGap(FxDifficultyRules r, double monthlyInflation) => Math.Max(0, r.GapBase + (r.GapInflationWeight * monthlyInflation));

    /// <summary>Un día: el oficial acompaña la inflación (crawl) con ruido; la brecha vuelve a su media.</summary>
    public void Step(EconomicDate date, FxDifficultyRules r, double monthlyInflation, DeterministicRandom rng)
    {
        double dailyInflation = Math.Pow(1 + monthlyInflation, 1.0 / EconomicDate.DaysPerSeason) - 1;
        _s.Official *= (1 + (r.Crawl * dailyInflation)) * Math.Exp(r.Volatility * rng.NextGaussian());
        if (r.Cepo)
        {
            double target = TargetGap(r, monthlyInflation);
            _s.Gap = Math.Clamp(_s.Gap + (r.GapReversion * (target - _s.Gap)) + (r.GapVolatility * rng.NextGaussian()), 0, 3);
        }
        else
        {
            _s.Gap = 0;
        }

        _s.History.Add(new FxClose(date, _s.Official, Mep, Blue(r)));
        if (_s.History.Count > EconomicDate.DaysPerYear * 2)
        {
            _s.History.RemoveAt(0);
        }
    }

    /// <summary>Al empezar la estación: ¿devaluación? Devuelve el salto (0 si no hubo).</summary>
    public double RollDevaluation(EconomicDate date, FxDifficultyRules r, DeterministicRandom rng)
    {
        double roll = rng.NextDouble();
        double size = r.DevaluationMin + ((r.DevaluationMax - r.DevaluationMin) * rng.NextDouble());
        return roll < r.DevaluationProbability ? Devalue(date, size) : 0;
    }

    /// <summary>Devaluación: sube el oficial; el MEP casi no se mueve, así que la brecha se achica.</summary>
    public double Devalue(EconomicDate date, double size)
    {
        double before = _s.Official;
        double mep = Mep;
        _s.Official *= 1 + size;
        _s.Gap = Math.Max(0, (mep * (1 + (size * 0.25)) / _s.Official) - 1);
        _s.Devaluations.Add(new FxDevaluation(date, size, before, _s.Official));
        return size;
    }

    /// <summary>Choque a la brecha (emisión, default soberano).</summary>
    public void WidenGap(double delta, FxDifficultyRules r)
    {
        if (r.Cepo)
        {
            _s.Gap = Math.Clamp(_s.Gap + delta, 0, 3);
        }
    }
}
