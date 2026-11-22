using ZuzuMarket.Core.Commodities;
using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Events;
using ZuzuMarket.Core.Ledger;
using ZuzuMarket.Core.Macro;
using ZuzuMarket.Core.Random;
using ZuzuMarket.Core.State;
using ZuzuMarket.Core.Taxation;
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

/// <summary>Qué pasó al empezar un día (para que el juego muestre noticias y reaccione).</summary>
public sealed record DayStartedInfo(
    EconomicDate Date,
    bool IsBusinessDay,
    bool IsNewFiscalPeriod,
    bool IsNewFiscalYear,
    int DaysAdvanced,
    IReadOnlyList<EventDefinition> NewEvents,
    IReadOnlyList<InflationRecord> PublishedInflation,
    IReadOnlyList<TaxNotice> TaxNotices);

/// <summary>
/// Orquestador: avanza el mundo económico un día por vez.
/// Orden de cada día: período fiscal (inflación, declaraciones y boletas) → eventos → IPC → precios → mora.
/// </summary>
public sealed class EconomySimulation
{
    public const string InflationStream = "inflacion";
    public const string EventsStream = "eventos";

    private readonly WorldState _state;
    private readonly ISimulationLog _log;

    private EconomySimulation(GameData data, WorldState state, GeneralLedger ledger, ISimulationLog log)
    {
        Data = data;
        _state = state;
        Ledger = ledger;
        _log = log;
        Inflation = new InflationModel(state.Inflation);
        Market = new CommodityMarket(data, state.Commodities);
        Events = new EventEngine(data, state.Events);
        Taxes = new TaxEngine(data, state.Taxes, ledger, Difficulty);

        if (!Inflation.HasStarted)
        {
            // Partida nueva, o partida del esquema 1 (Fase 0) que recién migra.
            Inflation.StartPeriod(state.CurrentDate.FiscalPeriod, Difficulty, RandomStreams.For(state.Seed, state.CurrentDate, InflationStream));
        }

        RecalculatePrices();
    }

    public GameData Data { get; }

    public GeneralLedger Ledger { get; }

    public InflationModel Inflation { get; }

    public CommodityMarket Market { get; }

    public EventEngine Events { get; }

    public TaxEngine Taxes { get; }

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

    /// <summary>
    /// Se llama al empezar cada día del juego. Simula un paso por cada día transcurrido; los días salteados
    /// se simulan con clima soleado.
    /// </summary>
    public DayStartedInfo OnDayStarted(EconomicDate date, Weather weather = Weather.Sun, FarmSnapshot? farm = null)
    {
        farm ??= FarmSnapshot.Empty;
        EconomicDate previous = _state.CurrentDate;
        int daysAdvanced = previous.DaysUntil(date);
        var newEvents = new List<EventDefinition>();
        var published = new List<InflationRecord>();
        var taxNotices = new List<TaxNotice>();

        if (daysAdvanced < 0)
        {
            _log.Warn($"La fecha del juego ({date}) es anterior a la última fecha simulada ({previous}). Se toma la fecha del juego sin simular.");
            _state.CurrentDate = date;
        }
        else
        {
            if (daysAdvanced > 1)
            {
                _log.Warn($"Se saltearon {daysAdvanced - 1} día(s) entre {previous} y {date}; se simulan igual.");
            }

            for (int i = 1; i <= daysAdvanced; i++)
            {
                EconomicDate day = previous.AddDays(i);
                StepDay(day, i == daysAdvanced ? weather : Weather.Sun, farm, newEvents, published, taxNotices);
            }
        }

        if (!Taxes.Started)
        {
            // Primer día que se simula la partida (nueva o migrada): alta como monotributista, sin período de gracia.
            taxNotices.AddRange(Taxes.Start(_state.CurrentDate, farm, (decimal)Inflation.Index));
        }

        bool newPeriod = daysAdvanced > 0 && date.FiscalPeriod != previous.FiscalPeriod;
        bool newYear = daysAdvanced > 0 && date.Year != previous.Year;
        return new DayStartedInfo(date, date.IsBusinessDay, newPeriod, newYear, daysAdvanced, newEvents, published, taxNotices);
    }

    /// <summary>Se llama al terminar el día, antes de guardar.</summary>
    public void OnDayEnding()
    {
        // Fases siguientes: cierre de mercado, liquidaciones, devengamientos, impuestos, vencimientos.
    }

    /// <summary>Registra unidades vendidas por el jugador como oferta (sin contabilizar). Impactan en el precio del día siguiente.</summary>
    public bool RecordShipment(string itemId, int units) => Market.RecordShipment(itemId, units);

    /// <summary>
    /// Ventas del cajón de envíos al terminar el día: suman oferta y se contabilizan como venta a cobrar
    /// (el oro llega a la noche y se cobra al conciliar).
    /// </summary>
    public void RecordShipmentSales(IReadOnlyCollection<SoldItem> items) => RecordSales(items, SaleChannel.Shipping);

    /// <summary>Ventas directas a una tienda: suman oferta y se cobran en el momento.</summary>
    public void RecordShopSales(IReadOnlyCollection<SoldItem> items) => RecordSales(items, SaleChannel.Shop);

    /// <summary>Compra en una tienda (o a Robin) por el importe pagado.</summary>
    public JournalEntry? RecordPurchase(long gross) => Taxes.RecordPurchase(_state.CurrentDate, gross);

    /// <summary>Paga una boleta contra la Caja. Devuelve el oro que el juego debe descontar, o null si no se pudo.</summary>
    public long? PayObligation(long obligationId) => Taxes.Pay(obligationId, _state.CurrentDate);

    /// <summary>Hace empezar un evento hoy y recalcula los precios (comando de debug).</summary>
    public EventDefinition? ForceEvent(string eventId)
    {
        EventDefinition? e = Data.FindEvent(eventId);
        if (e is null)
        {
            return null;
        }

        Events.Start(e, _state.CurrentDate);
        if (e.InflationShock != 0)
        {
            Inflation.AddShock(e.InflationShock);
        }

        RecalculatePrices();
        return e;
    }

    /// <summary>
    /// Compara el oro real del jugador con la cuenta Caja y asienta la diferencia como movimiento no clasificado.
    /// Devuelve el asiento, o <c>null</c> si no había diferencia.
    /// </summary>
    /// <remarks>Primero cobra las ventas del cajón de envíos pendientes (Deudores por ventas).</remarks>
    public JournalEntry? ReconcileCash(long playerGold, EntryText description)
    {
        decimal cash = Ledger.GetBalance(LedgerEntities.Jugador, SystemAccounts.Caja);
        decimal difference = playerGold - cash;
        JournalEntry? last = null;

        decimal receivable = Ledger.GetBalance(LedgerEntities.Jugador, SystemAccounts.DeudoresPorVentas);
        if (difference > 0 && receivable > 0)
        {
            decimal collected = Math.Min(difference, receivable);
            last = Ledger.Post(
                LedgerEntities.Jugador,
                _state.CurrentDate,
                new EntryText("Cobro de ventas del cajón de envíos", "entry.receivable-collected"),
                EntrySources.Cobro,
                JournalLine.ToDebit(SystemAccounts.Caja, collected),
                JournalLine.ToCredit(SystemAccounts.DeudoresPorVentas, collected));
            difference -= collected;
        }

        if (difference == 0)
        {
            return last;
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

    private void RecordSales(IReadOnlyCollection<SoldItem> items, SaleChannel channel)
    {
        foreach (SoldItem item in items)
        {
            Market.RecordShipment(item.ItemId, item.Units);
        }

        Taxes.RecordSales(_state.CurrentDate, items, channel);
    }

    private void StepDay(EconomicDate day, Weather weather, FarmSnapshot farm, List<EventDefinition> newEvents, List<InflationRecord> published, List<TaxNotice> taxNotices)
    {
        _state.CurrentDate = day;
        DifficultyDefinition difficulty = Difficulty;

        if (_state.Inflation.CurrentPeriod != day.FiscalPeriod)
        {
            InflationRecord? record = Inflation.ClosePeriod();
            if (record is not null)
            {
                published.Add(record);
                Events.AddNews(new NewsItem(day, NewsKind.Inflation, record.Period.ToString(), record.Rate));
            }

            Inflation.StartPeriod(day.FiscalPeriod, difficulty, RandomStreams.For(_state.Seed, day, InflationStream));
        }

        if (Taxes.Started)
        {
            taxNotices.AddRange(Taxes.OnPeriodStart(day, farm, (decimal)Inflation.Index));
        }

        Events.Expire(day);
        foreach (EventDefinition e in Events.Roll(day, weather, difficulty, RandomStreams.For(_state.Seed, day, EventsStream)))
        {
            newEvents.Add(e);
            if (e.InflationShock != 0)
            {
                Inflation.AddShock(e.InflationShock);
            }
        }

        Inflation.AdvanceDay();
        Market.Step(day, Inflation.Index, (double)difficulty.PriceVolatility, _state.Seed, c => Events.PriceImpact(c, day));
        taxNotices.AddRange(Taxes.OnDay(day));
    }

    private void RecalculatePrices()
    {
        EconomicDate today = _state.CurrentDate;
        Market.Recalculate(today, Inflation.Index, c => Events.PriceImpact(c, today));
    }
}
