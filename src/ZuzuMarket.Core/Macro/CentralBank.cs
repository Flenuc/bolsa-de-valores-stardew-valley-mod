using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Macro;

/// <summary>Parámetros de la regla de política monetaria (<c>markets.json</c> → <c>centralBank</c>).</summary>
public sealed class CentralBankRules
{
    /// <summary>Tasa real neutral mensual.</summary>
    public double NeutralRealRate { get; set; } = 0.004;

    /// <summary>Reacción a la inflación por encima de la meta (φ de la regla de Taylor).</summary>
    public double Phi { get; set; } = 0.5;

    public double Floor { get; set; } = 0.002;

    /// <summary>Máximo cambio por reunión (suavizado), en tasa mensual.</summary>
    public double MaxStep { get; set; } = 0.02;

    /// <summary>Suba extraordinaria ante una emisión monetaria.</summary>
    public double EmissionHike { get; set; } = 0.01;
}

public sealed record RateDecision(EconomicDate Date, double Rate, double Change);

public sealed class CentralBankState
{
    public bool Started { get; set; }

    /// <summary>Tasa de política mensual (por 28 días).</summary>
    public double PolicyRate { get; set; }

    public List<RateDecision> History { get; set; } = new();
}

/// <summary>Banco Central de la República de Ferngill: fija la tasa de política con una regla de Taylor suavizada.</summary>
public sealed class CentralBank
{
    private readonly CentralBankState _s;
    private readonly CentralBankRules _rules;

    public CentralBank(CentralBankState state, CentralBankRules rules)
    {
        _s = state;
        _rules = rules;
    }

    public CentralBankState State => _s;

    public bool Started => _s.Started;

    public double PolicyRate => _s.PolicyRate;

    /// <summary>Tasa que indica la regla para la inflación esperada (sin suavizar).</summary>
    public double TaylorRate(double expectedInflation, DifficultyDefinition difficulty)
    {
        double target = (double)difficulty.InflationMin;
        double rate = expectedInflation + _rules.NeutralRealRate + (_rules.Phi * (expectedInflation - target));
        return Math.Max(_rules.Floor, rate);
    }

    /// <summary>Primera tasa: directamente la de la regla.</summary>
    public RateDecision Start(EconomicDate date, double expectedInflation, DifficultyDefinition difficulty)
    {
        _s.Started = true;
        _s.PolicyRate = TaylorRate(expectedInflation, difficulty);
        var decision = new RateDecision(date, _s.PolicyRate, 0);
        _s.History.Add(decision);
        return decision;
    }

    /// <summary>Reunión de cada estación: se acerca a la regla sin moverse más de <see cref="CentralBankRules.MaxStep"/>.</summary>
    public RateDecision Meet(EconomicDate date, double expectedInflation, DifficultyDefinition difficulty)
    {
        double target = TaylorRate(expectedInflation, difficulty);
        double change = Math.Clamp(target - _s.PolicyRate, -_rules.MaxStep, _rules.MaxStep);
        return Set(date, Math.Max(_rules.Floor, _s.PolicyRate + change));
    }

    /// <summary>Suba extraordinaria (por ejemplo, ante una emisión monetaria).</summary>
    public RateDecision EmergencyHike(EconomicDate date) => Set(date, _s.PolicyRate + _rules.EmissionHike);

    private RateDecision Set(EconomicDate date, double rate)
    {
        var decision = new RateDecision(date, rate, rate - _s.PolicyRate);
        _s.PolicyRate = rate;
        _s.History.Add(decision);
        return decision;
    }
}
