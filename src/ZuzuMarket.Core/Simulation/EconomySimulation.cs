using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Ledger;
using ZuzuMarket.Core.Random;
using ZuzuMarket.Core.State;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Simulation;

/// <summary>Salida de log de la simulación (el mod la conecta al monitor de SMAPI).</summary>
public interface ISimulationLog
{
    void Info(string message);

    void Warn(string message);
}

public sealed class NullSimulationLog : ISimulationLog
{
    public static readonly NullSimulationLog Instance = new();

    public void Info(string message)
    {
    }

    public void Warn(string message)
    {
    }
}

/// <summary>Qué pasó al empezar un día (para que el juego reaccione).</summary>
public sealed record DayStartedInfo(
    EconomicDate Date,
    bool IsBusinessDay,
    bool IsNewFiscalPeriod,
    bool IsNewFiscalYear,
    int DaysAdvanced);

/// <summary>
/// Orquestador: avanza el mundo económico. En la Fase 0 solo lleva la fecha y la contabilidad;
/// las fases siguientes agregan macro, eventos, mercados e impuestos en estos mismos ganchos.
/// </summary>
public sealed class EconomySimulation
{
    private readonly WorldState _state;
    private readonly ISimulationLog _log;

    private EconomySimulation(GameData data, WorldState state, GeneralLedger ledger, ISimulationLog log)
    {
        Data = data;
        _state = state;
        Ledger = ledger;
        _log = log;
    }

    public GameData Data { get; }

    public GeneralLedger Ledger { get; }

    public ulong Seed => _state.Seed;

    public EconomicDate CurrentDate => _state.CurrentDate;

    public EconomicDate CreatedOn => _state.CreatedOn;

    public DifficultyDefinition Difficulty =>
        Data.FindDifficulty(_state.DifficultyId)
        ?? throw new InvalidOperationException($"Dificultad desconocida: {_state.DifficultyId}.");

    /// <summary>Empieza a simular una partida (nueva, o existente en la que se instaló el mod).</summary>
    public static EconomySimulation StartNewGame(
        GameData data,
        ulong seed,
        EconomicDate date,
        string difficultyId,
        long openingCash,
        ISimulationLog? log = null)
    {
        DifficultyDefinition difficulty = data.FindDifficulty(difficultyId)
            ?? throw new ArgumentException($"Dificultad desconocida: {difficultyId}.", nameof(difficultyId));

        var state = new WorldState
        {
            Seed = seed,
            CreatedOn = date,
            CurrentDate = date,
            DifficultyId = difficulty.Id,
        };

        var ledger = new GeneralLedger(data.Chart, state.Ledger);
        ledger.AddEntity(LedgerEntities.Jugador);
        ledger.AddEntity(LedgerEntities.Granja);

        if (openingCash > 0)
        {
            ledger.Post(
                LedgerEntities.Jugador,
                date,
                "Asiento de apertura: oro inicial del jugador",
                EntrySources.Apertura,
                JournalLine.ToDebit(SystemAccounts.Caja, openingCash),
                JournalLine.ToCredit(SystemAccounts.Capital, openingCash));
        }

        return new EconomySimulation(data, state, ledger, log ?? NullSimulationLog.Instance);
    }

    /// <summary>Retoma una simulación guardada.</summary>
    public static EconomySimulation Load(GameData data, WorldState state, ISimulationLog? log = null)
    {
        if (data.FindDifficulty(state.DifficultyId) is null)
        {
            throw new ArgumentException($"La partida usa una dificultad que ya no existe: {state.DifficultyId}.", nameof(state));
        }

        var ledger = new GeneralLedger(data.Chart, state.Ledger);
        ledger.AddEntity(LedgerEntities.Jugador);
        ledger.AddEntity(LedgerEntities.Granja);
        return new EconomySimulation(data, state, ledger, log ?? NullSimulationLog.Instance);
    }

    /// <summary>Estado para guardar.</summary>
    public WorldState Snapshot() => _state;

    public DeterministicRandom Random(string streamName) => RandomStreams.For(_state.Seed, _state.CurrentDate, streamName);

    /// <summary>Se llama al empezar cada día del juego.</summary>
    public DayStartedInfo OnDayStarted(EconomicDate date)
    {
        EconomicDate previous = _state.CurrentDate;
        int daysAdvanced = previous.DaysUntil(date);
        if (daysAdvanced < 0)
        {
            _log.Warn($"La fecha del juego ({date}) es anterior a la última fecha simulada ({previous}). Se toma la fecha del juego.");
        }
        else if (daysAdvanced > 1)
        {
            _log.Warn($"Se saltearon {daysAdvanced - 1} día(s) entre {previous} y {date}.");
        }

        _state.CurrentDate = date;
        bool newPeriod = daysAdvanced > 0 && date.FiscalPeriod != previous.FiscalPeriod;
        bool newYear = daysAdvanced > 0 && date.Year != previous.Year;

        // Fases siguientes: macro (inflación, tasa), eventos del día, apertura de mercado.
        return new DayStartedInfo(date, date.IsBusinessDay, newPeriod, newYear, daysAdvanced);
    }

    /// <summary>Se llama al terminar el día, antes de guardar.</summary>
    public void OnDayEnding()
    {
        // Fases siguientes: cierre de mercado, liquidaciones, devengamientos, impuestos, vencimientos.
    }

    /// <summary>
    /// Compara el oro real del jugador con la cuenta Caja y asienta la diferencia como movimiento no clasificado.
    /// Devuelve el asiento, o <c>null</c> si no había diferencia.
    /// </summary>
    public JournalEntry? ReconcileCash(long playerGold, string description)
    {
        decimal cash = Ledger.GetBalance(LedgerEntities.Jugador, SystemAccounts.Caja);
        decimal difference = playerGold - cash;
        if (difference == 0)
        {
            return null;
        }

        JournalLine[] lines = difference > 0
            ? new[]
            {
                JournalLine.ToDebit(SystemAccounts.Caja, difference),
                JournalLine.ToCredit(SystemAccounts.IngresosNoClasificados, difference),
            }
            : new[]
            {
                JournalLine.ToDebit(SystemAccounts.EgresosNoClasificados, -difference),
                JournalLine.ToCredit(SystemAccounts.Caja, -difference),
            };

        return Ledger.Post(LedgerEntities.Jugador, _state.CurrentDate, description, EntrySources.Conciliacion, lines);
    }
}
