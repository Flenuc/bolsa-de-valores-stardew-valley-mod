using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Ledger;
using ZuzuMarket.Core.Simulation;
using ZuzuMarket.Core.State;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Tests.Simulation;

public class EconomySimulationTests
{
    private static readonly EconomicDate Start = new(1, Season.Spring, 1);

    [Fact]
    public void StartNewGame_PostsOpeningEntry()
    {
        EconomySimulation sim = EconomySimulation.StartNewGame(TestData.Real, 777, Start, "realista", 500);

        Assert.Equal(500m, sim.Ledger.GetBalance(LedgerEntities.Jugador, SystemAccounts.Caja));
        Assert.Equal(500m, sim.Ledger.GetBalance(LedgerEntities.Jugador, SystemAccounts.Capital));
        JournalEntry opening = Assert.Single(sim.Ledger.Entries);
        Assert.Equal(EntrySources.Apertura, opening.Source);
        Assert.Equal("realista", sim.Difficulty.Id);
        Assert.True(sim.Ledger.HasEntity(LedgerEntities.Granja));
    }

    [Fact]
    public void StartNewGame_WithZeroGold_HasNoEntries()
    {
        EconomySimulation sim = EconomySimulation.StartNewGame(TestData.Real, 1, Start, "casual", 0);
        Assert.Empty(sim.Ledger.Entries);
    }

    [Fact]
    public void StartNewGame_UnknownDifficulty_Throws()
    {
        Assert.Throws<ArgumentException>(() => EconomySimulation.StartNewGame(TestData.Real, 1, Start, "imposible", 0));
    }

    [Fact]
    public void ReconcileCash_PostsIncomeAndExpense()
    {
        EconomySimulation sim = EconomySimulation.StartNewGame(TestData.Real, 1, Start, "realista", 500);

        JournalEntry? income = sim.ReconcileCash(800, "Venta");
        Assert.NotNull(income);
        Assert.Equal(300m, sim.Ledger.GetBalance(LedgerEntities.Jugador, SystemAccounts.IngresosNoClasificados));

        JournalEntry? expense = sim.ReconcileCash(650, "Compra");
        Assert.NotNull(expense);
        Assert.Equal(150m, sim.Ledger.GetBalance(LedgerEntities.Jugador, SystemAccounts.EgresosNoClasificados));
        Assert.Equal(650m, sim.Ledger.GetBalance(LedgerEntities.Jugador, SystemAccounts.Caja));

        Assert.Null(sim.ReconcileCash(650, "Sin cambios"));
        Assert.Equal(3, sim.Ledger.Entries.Count);
    }

    [Fact]
    public void OnDayStarted_DetectsNewPeriodAndYear()
    {
        EconomySimulation sim = EconomySimulation.StartNewGame(TestData.Real, 1, new EconomicDate(1, Season.Winter, 27), "realista", 0);

        DayStartedInfo sameDay = sim.OnDayStarted(new EconomicDate(1, Season.Winter, 27));
        Assert.Equal(0, sameDay.DaysAdvanced);
        Assert.False(sameDay.IsNewFiscalPeriod);

        DayStartedInfo lastDay = sim.OnDayStarted(new EconomicDate(1, Season.Winter, 28));
        Assert.False(lastDay.IsNewFiscalPeriod);
        Assert.False(lastDay.IsBusinessDay);

        DayStartedInfo newYear = sim.OnDayStarted(new EconomicDate(2, Season.Spring, 1));
        Assert.True(newYear.IsNewFiscalPeriod);
        Assert.True(newYear.IsNewFiscalYear);
        Assert.True(newYear.IsBusinessDay);
        Assert.Equal(new EconomicDate(2, Season.Spring, 1), sim.CurrentDate);
    }

    [Fact]
    public void OnDayStarted_WarnsWhenTimeGoesBackwards()
    {
        var log = new RecordingLog();
        EconomySimulation sim = EconomySimulation.StartNewGame(TestData.Real, 1, new EconomicDate(1, Season.Summer, 10), "realista", 0, log);
        sim.OnDayStarted(new EconomicDate(1, Season.Summer, 5));
        Assert.Contains(log.Warnings, w => w.Contains("anterior"));
        Assert.Equal(new EconomicDate(1, Season.Summer, 5), sim.CurrentDate);
    }

    [Fact]
    public void Random_DependsOnSeedDateAndStream()
    {
        EconomySimulation a = EconomySimulation.StartNewGame(TestData.Real, 5, Start, "realista", 0);
        EconomySimulation b = EconomySimulation.StartNewGame(TestData.Real, 5, Start, "realista", 0);
        Assert.Equal(a.Random("eventos").NextUInt64(), b.Random("eventos").NextUInt64());
        Assert.NotEqual(a.Random("eventos").NextUInt64(), a.Random("precios").NextUInt64());
    }

    [Fact]
    public void SaveAndLoad_RoundTripsEverything()
    {
        EconomySimulation original = EconomySimulation.StartNewGame(TestData.Real, ulong.MaxValue - 3, Start, "argentina", 500);
        original.OnDayStarted(new EconomicDate(1, Season.Spring, 2));
        original.ReconcileCash(1234, "Venta de chirivías");

        string json = WorldStateSerializer.Serialize(original.Snapshot());
        EconomySimulation loaded = EconomySimulation.Load(TestData.Real, WorldStateSerializer.Deserialize(json));

        Assert.Equal(original.Seed, loaded.Seed);
        Assert.Equal(original.CurrentDate, loaded.CurrentDate);
        Assert.Equal(original.CreatedOn, loaded.CreatedOn);
        Assert.Equal("argentina", loaded.Difficulty.Id);
        Assert.Equal(1234m, loaded.Ledger.GetBalance(LedgerEntities.Jugador, SystemAccounts.Caja));
        Assert.Equal(
            original.Ledger.Entries.Select(e => (e.Id, e.Description, e.Date, e.TotalDebit)),
            loaded.Ledger.Entries.Select(e => (e.Id, e.Description, e.Date, e.TotalDebit)));

        long lastId = loaded.Ledger.Entries[^1].Id;
        JournalEntry? next = loaded.ReconcileCash(1000, "Compra");
        Assert.Equal(lastId + 1, next!.Id);
        Assert.Equal(original.Random("x").NextUInt64(), loaded.Random("x").NextUInt64());
    }

    [Fact]
    public void Deserialize_RejectsNewerSchemaAndGarbage()
    {
        string json = WorldStateSerializer.Serialize(new WorldState { SchemaVersion = WorldState.CurrentSchemaVersion + 1 });
        Assert.Throws<WorldStateSerializationException>(() => WorldStateSerializer.Deserialize(json));
        Assert.Throws<WorldStateSerializationException>(() => WorldStateSerializer.Deserialize("{ roto"));
        Assert.Throws<WorldStateSerializationException>(() => WorldStateSerializer.Deserialize("null"));
    }

    [Fact]
    public void SkippingDays_GivesTheSameResultAsAdvancingDayByDay()
    {
        EconomySimulation stepByStep = EconomySimulation.StartNewGame(TestData.Real, 11, Start, "realista", 0);
        EconomySimulation jumping = EconomySimulation.StartNewGame(TestData.Real, 11, Start, "realista", 0);
        var target = new EconomicDate(1, Season.Summer, 10);

        TestData.AdvanceTo(stepByStep, target);
        DayStartedInfo info = jumping.OnDayStarted(target);

        Assert.Equal(target.AbsoluteDay, info.DaysAdvanced);
        Assert.Equal(stepByStep.Inflation.Index, jumping.Inflation.Index);
        foreach (string id in stepByStep.Market.States.Keys)
        {
            Assert.Equal(stepByStep.Market.Get(id).Multiplier, jumping.Market.Get(id).Multiplier);
        }

        Assert.Equal(stepByStep.Events.State.News, jumping.Events.State.News);
    }

    [Fact]
    public void SameSeedAndSameSales_GiveIdenticalHistories()
    {
        EconomySimulation a = EconomySimulation.StartNewGame(TestData.Real, 31, Start, "argentina", 0);
        EconomySimulation b = EconomySimulation.StartNewGame(TestData.Real, 31, Start, "argentina", 0);
        for (int day = 0; day < 200; day++)
        {
            foreach (EconomySimulation sim in new[] { a, b })
            {
                sim.RecordShipment("24", day % 50);
                sim.RecordShipment("378", day % 7 * 30);
                sim.OnDayStarted(sim.CurrentDate.AddDays(1), day % 5 == 0 ? Weather.Rain : Weather.Sun);
            }
        }

        foreach (string id in a.Market.States.Keys)
        {
            Assert.Equal(a.Market.Get(id).History, b.Market.Get(id).History);
        }

        Assert.Equal(a.Inflation.State.History, b.Inflation.State.History);
    }

    [Fact]
    public void SavingAndLoading_DoesNotChangeTheFuture()
    {
        EconomySimulation original = EconomySimulation.StartNewGame(TestData.Real, 77, Start, "realista", 100);
        TestData.AdvanceTo(original, new EconomicDate(1, Season.Summer, 3));
        original.RecordShipment("256", 300);
        original.ForceEvent("ola-calor");

        EconomySimulation loaded = EconomySimulation.Load(TestData.Real, WorldStateSerializer.Deserialize(WorldStateSerializer.Serialize(original.Snapshot())));
        Assert.Equal(original.Market.Get("leche").Multiplier, loaded.Market.Get("leche").Multiplier);
        Assert.True(loaded.Events.IsActive("ola-calor"));

        var target = new EconomicDate(1, Season.Fall, 20);
        TestData.AdvanceTo(original, target);
        TestData.AdvanceTo(loaded, target);
        Assert.Equal(original.Inflation.Index, loaded.Inflation.Index);
        foreach (string id in original.Market.States.Keys)
        {
            Assert.Equal(original.Market.Get(id).Multiplier, loaded.Market.Get(id).Multiplier);
        }
    }

    [Fact]
    public void SchemaOneSave_FromPhaseZero_MigratesAndStartsTheEconomy()
    {
        const string phaseZeroSave = """
            {"schemaVersion":1,"seed":455031588,
             "createdOn":{"year":1,"season":"Spring","day":1},
             "currentDate":{"year":1,"season":"Spring","day":2},
             "difficultyId":"realista",
             "ledger":{"entities":["jugador","granja"],"nextEntryId":3,"entries":[
               {"id":1,"entityId":"jugador","date":{"year":1,"season":"Spring","day":1},"description":"Apertura","source":"apertura",
                "lines":[{"accountCode":"1.1.01","debit":500,"credit":0},{"accountCode":"3.1.01","debit":0,"credit":500}]},
               {"id":2,"entityId":"jugador","date":{"year":1,"season":"Spring","day":1},"description":"Venta","source":"conciliacion",
                "lines":[{"accountCode":"1.1.01","debit":150,"credit":0},{"accountCode":"4.9.01","debit":0,"credit":150}]}]}}
            """;

        WorldState state = WorldStateSerializer.Deserialize(phaseZeroSave);
        Assert.Equal(WorldState.CurrentSchemaVersion, state.SchemaVersion);

        EconomySimulation sim = EconomySimulation.Load(TestData.Real, state);
        Assert.Equal(650m, sim.Ledger.GetBalance(LedgerEntities.Jugador, SystemAccounts.Caja));
        Assert.Equal(1.0, sim.Inflation.Index);
        Assert.True(sim.Inflation.HasStarted);
        Assert.Equal(15, sim.Market.States.Count);
        Assert.Equal(0.95, sim.Market.Get("chirivia").Multiplier, 10);
        Assert.Empty(sim.Events.Active);

        Assert.False(sim.Taxes.Started);
        DayStartedInfo info = sim.OnDayStarted(new EconomicDate(1, Season.Spring, 3));
        Assert.Single(sim.Market.Get("chirivia").History);

        // Esquema 2 → 3: el primer día simulado se da de alta como monotributista, sin período de gracia.
        Assert.True(sim.Taxes.Started);
        Assert.Contains(info.TaxNotices, n => n.Kind == ZuzuMarket.Core.Taxation.TaxNoticeKind.Registered);
        Assert.Equal(2, sim.Taxes.State.Obligations.Count);
    }

    private sealed class RecordingLog : ISimulationLog
    {
        public List<string> Warnings { get; } = new();

        public void Info(string message)
        {
        }

        public void Warn(string message) => Warnings.Add(message);
    }
}
