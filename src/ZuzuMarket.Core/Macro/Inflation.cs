using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Random;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Macro;

/// <summary>Inflación publicada por el INDEF al cerrar un período fiscal.</summary>
public sealed record InflationRecord(FiscalPeriod Period, double Rate, double IndexAtClose);

/// <summary>Estado serializable del índice de precios (IPC-F).</summary>
public sealed class InflationState
{
    /// <summary>Índice de precios. Vale 1 el día en que el mod empezó a simular la partida.</summary>
    public double Index { get; set; } = 1.0;

    /// <summary>Inflación mensual (por estación) vigente, como fracción.</summary>
    public double PeriodRate { get; set; }

    /// <summary>Período en curso; <c>null</c> si todavía no empezó ninguno.</summary>
    public FiscalPeriod? CurrentPeriod { get; set; }

    /// <summary>Índice al empezar el período en curso (para medir la inflación realizada).</summary>
    public double PeriodStartIndex { get; set; } = 1.0;

    public List<InflationRecord> History { get; set; } = new();
}

/// <summary>Modelo de inflación: una tasa mensual sorteada por período, más shocks de eventos.</summary>
public sealed class InflationModel
{
    private readonly InflationState _state;

    public InflationModel(InflationState state)
    {
        _state = state;
    }

    public InflationState State => _state;

    public double Index => _state.Index;

    public double PeriodRate => _state.PeriodRate;

    public bool HasStarted => _state.CurrentPeriod is not null;

    /// <summary>Empieza un período: sortea la tasa en el rango de la dificultad.</summary>
    public void StartPeriod(FiscalPeriod period, DifficultyDefinition difficulty, DeterministicRandom rng)
    {
        double min = (double)difficulty.InflationMin;
        double max = (double)difficulty.InflationMax;
        _state.CurrentPeriod = period;
        _state.PeriodRate = min + ((max - min) * rng.NextDouble());
        _state.PeriodStartIndex = _state.Index;
    }

    /// <summary>Cierra el período en curso y publica la inflación realizada.</summary>
    public InflationRecord? ClosePeriod()
    {
        if (_state.CurrentPeriod is not FiscalPeriod period)
        {
            return null;
        }

        var record = new InflationRecord(period, (_state.Index / _state.PeriodStartIndex) - 1.0, _state.Index);
        _state.History.Add(record);
        return record;
    }

    /// <summary>Suma un shock (p. ej. emisión monetaria) a la tasa del período en curso.</summary>
    public void AddShock(double shock)
    {
        _state.PeriodRate = Math.Max(-0.5, _state.PeriodRate + shock);
    }

    /// <summary>Avanza un día: el índice crece a la tasa mensual repartida en 28 días.</summary>
    public void AdvanceDay()
    {
        _state.Index *= Math.Pow(1.0 + _state.PeriodRate, 1.0 / EconomicDate.DaysPerSeason);
    }
}
