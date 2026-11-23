using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Events;
using ZuzuMarket.Core.Ledger;
using ZuzuMarket.Core.Markets;
using ZuzuMarket.Core.Simulation;
using ZuzuMarket.Core.State;
using ZuzuMarket.Core.Taxation;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Tests.Macro;

public class ExchangeRateTests
{
    private static readonly EconomicDate Monday = new(1, Season.Spring, 1);
    private const int TenAm = 1000;

    private static EconomySimulation NewGame(string difficulty, decimal deposit = 0, GameData? data = null)
    {
        EconomySimulation sim = EconomySimulation.StartNewGame(data ?? TestData.Load(), 5, Monday, difficulty, 10_000_000);
        sim.OnDayStarted(Monday);
        if (deposit > 0)
        {
            sim.Broker.Open(Monday);
            sim.Broker.Deposit(deposit, Monday);
        }

        return sim;
    }

    private static void Advance(EconomySimulation sim, EconomicDate target)
    {
        while (sim.CurrentDate < target)
        {
            sim.OnDayStarted(sim.CurrentDate.AddDays(1));
        }
    }

    private static void AssertBalanced(EconomySimulation sim)
    {
        IReadOnlyList<TrialBalanceRow> rows = sim.Ledger.GetTrialBalance(LedgerEntities.Jugador);
        Assert.Equal(rows.Sum(r => r.Debits), rows.Sum(r => r.Credits));
    }

    // ───────────────────────────── Cotizaciones ─────────────────────────────

    [Theory]
    [InlineData("casual")]
    [InlineData("realista")]
    public void WithoutCepo_ThereIsNoGapAndTheOfficialFollowsInflation(string difficulty)
    {
        EconomySimulation sim = NewGame(difficulty);
        double ipc0 = sim.Inflation.Index;
        Advance(sim, new EconomicDate(2, Season.Spring, 1));
        Assert.Equal(0, sim.Fx.Gap);
        Assert.Equal(sim.Fx.Official, sim.Fx.Mep, 9);
        double inflation = sim.Inflation.Index / ipc0;
        double depreciation = sim.Fx.Official / 50;
        Assert.InRange(depreciation / inflation, 0.85, 1.25); // tipo de cambio real más o menos estable
        Assert.Equal(decimal.MaxValue, sim.Broker.QuotaLeft(sim.CurrentDate));
    }

    [Fact]
    public void Argentina_HasAGap_AndTheOfficialLagsInflation()
    {
        EconomySimulation sim = NewGame("argentina", data: TestData.WithoutEvents());
        double ipc0 = sim.Inflation.Index;
        Advance(sim, new EconomicDate(1, Season.Winter, 28));
        Assert.InRange(sim.Fx.Gap, 0.2, 3.0);
        Assert.True(sim.Fx.Mep > sim.Fx.Official);
        Assert.True(sim.Fx.Blue(sim.FxRules) > sim.Fx.Mep);
        Assert.True(sim.Fx.State.History.Count > 100);
    }

    [Fact]
    public void Devaluation_JumpsTheOfficial_NarrowsTheGap_AndMakesNews()
    {
        EconomySimulation sim = NewGame("argentina");
        double official = sim.Fx.Official;
        double gap = sim.Fx.Gap;
        double inflation = sim.Inflation.PeriodRate;
        double policy = sim.CentralBank.PolicyRate;
        double gotr = sim.Stocks.Get("GOTR").Close;
        double joja = sim.Stocks.Get("JOJA").Close;

        sim.ForceDevaluation(0.5);
        Assert.Equal(official * 1.5, sim.Fx.Official, 6);
        Assert.True(sim.Fx.Gap < gap);
        Assert.True(sim.Inflation.PeriodRate > inflation);
        Assert.True(sim.CentralBank.PolicyRate > policy);
        Assert.True(sim.Stocks.Get("GOTR").Close > gotr);
        Assert.True(sim.Stocks.Get("JOJA").Close < joja);
        Assert.Contains(sim.Events.State.News, n => n.Kind == NewsKind.Devaluation);
    }

    // ───────────────────────────── Oficial y cuenta en oro gotoro ─────────────────────────────

    [Fact]
    public void Argentina_OfficialHasQuotaAndSurcharge()
    {
        EconomySimulation sim = NewGame("argentina", deposit: 100_000);
        decimal cash = sim.Broker.Cash;
        Assert.Equal(TradeError.AboveLimit, sim.Broker.BuyOfficial(201, Monday, TenAm));
        Assert.Equal(TradeError.None, sim.Broker.BuyOfficial(150, Monday, TenAm));
        Assert.Equal(150m, sim.Broker.Gtd);
        Assert.Equal(cash - Math.Round(150 * sim.Broker.Official * 1.6m, 2), sim.Broker.Cash, 1);
        Assert.Equal(50m, sim.Broker.QuotaLeft(Monday));
        Assert.Equal(TradeError.AboveLimit, sim.Broker.BuyOfficial(51, Monday, TenAm));
        Assert.True(sim.Ledger.GetBalance(LedgerEntities.Jugador, SystemAccounts.ImpuestoDivisas) > 0);

        // La estación siguiente se renueva el cupo.
        Assert.Equal(200m, sim.Broker.QuotaLeft(new EconomicDate(1, Season.Summer, 1)));
        AssertBalanced(sim);
    }

    [Fact]
    public void GotoroAccount_IsRevaluedDaily()
    {
        EconomySimulation sim = NewGame("argentina", deposit: 100_000);
        sim.Broker.BuyOfficial(100, Monday, TenAm);
        sim.ForceDevaluation(0.4);
        sim.OnDayStarted(Monday.AddDays(1));
        decimal value = sim.Ledger.GetBalance(LedgerEntities.Jugador, SystemAccounts.CuentaGotoro);
        Assert.Equal(Math.Round(100 * sim.Broker.Official, 2), value);
        Assert.True(sim.Ledger.GetBalance(LedgerEntities.Jugador, SystemAccounts.DiferenciaCambioPositiva) > 0);
        AssertBalanced(sim);
    }

    [Fact]
    public void Realista_OfficialHasNoQuotaNorSurcharge()
    {
        EconomySimulation sim = NewGame("realista", deposit: 1_000_000);
        Assert.Equal(TradeError.None, sim.Broker.BuyOfficial(5_000, Monday, TenAm));
        Assert.Equal(0m, sim.Ledger.GetBalance(LedgerEntities.Jugador, SystemAccounts.ImpuestoDivisas));
        Assert.Equal(TradeError.None, sim.Broker.SellOfficial(5_000, Monday, TenAm));
        Assert.Equal(0m, sim.Broker.Gtd);
        AssertBalanced(sim);
    }

    // ───────────────────────────── MEP ─────────────────────────────

    [Fact]
    public void Mep_BuysTheBondInLocalGold_AndSellsItInGotoroAfterParking()
    {
        EconomySimulation sim = NewGame("argentina", deposit: 1_000_000);
        BondSeries bond = sim.Broker.MepBond!;
        Assert.True(bond.IsGotoro);
        decimal mep = sim.Broker.Mep;
        Assert.Equal(TradeError.None, sim.Broker.BuyMep(100_000, Monday, TenAm));
        Assert.Equal(0m, sim.Broker.Gtd); // parking: todavía no
        Assert.Single(sim.Broker.State.MepOrders);

        sim.OnDayStarted(Monday.AddDays(1));
        Assert.Empty(sim.Broker.State.MepOrders);
        Assert.InRange(sim.Broker.Gtd, 100_000 / mep * 0.95m, 100_000 / mep * 1.01m); // ≈ al precio MEP, menos las comisiones de las dos patas
        Assert.False(sim.Broker.State.Bonds.ContainsKey(bond.Id));
        AssertBalanced(sim);
    }

    [Fact]
    public void Mep_Reverse_TurnsGotoroIntoLocalGold()
    {
        EconomySimulation sim = NewGame("argentina", deposit: 100_000);
        sim.Broker.BuyOfficial(200, Monday, TenAm);
        decimal cash = sim.Broker.Cash;
        Assert.Equal(TradeError.None, sim.Broker.SellMep(200, Monday, TenAm));
        Assert.True(sim.Broker.Gtd < 200);
        sim.OnDayStarted(Monday.AddDays(1));
        // Vendido al MEP (con brecha): más oro local que lo que costó al oficial sin recargo.
        Assert.True(sim.Broker.Cash - cash > 190 * sim.Broker.Official);
        AssertBalanced(sim);
    }

    [Fact]
    public void GotoroBonds_PayCouponsInGotoro()
    {
        EconomySimulation sim = NewGame("argentina", deposit: 1_000_000);
        BondSeries bond = sim.Broker.MepBond!;
        Assert.Equal(TradeError.None, sim.Broker.Execute(bond.Id, TradeSide.Buy, 50, Monday, TenAm).Error);
        Advance(sim, new EconomicDate(1, Season.Summer, 2));
        Assert.True(sim.Broker.Gtd > 0); // el cupón entró en oro gotoro
        AssertBalanced(sim);
    }

    // ───────────────────────────── Comercio y blue ─────────────────────────────

    [Fact]
    public void Argentina_ExportsSettleAtTheBlend_AndImportsAtTheOfficial()
    {
        EconomySimulation arg = NewGame("argentina");
        EconomySimulation real = NewGame("realista");
        Assert.Equal(1, real.ExportLiquidationFactor);
        Assert.Equal((0.8 / (1 + arg.Fx.Gap)) + 0.2, arg.ExportLiquidationFactor, 9);
        Assert.True(arg.ExportLiquidationFactor < 1);

        double withGap = arg.PurchaseMultiplier("Sandy") / arg.Inflation.Index;
        Assert.Equal((double)(1 + arg.Data.Trade.ImportSurcharge(arg.Difficulty.TradeDuties)) * 1.3 / (1 + arg.Fx.Gap), withGap, 6);
    }

    [Fact]
    public void BlueRaid_CanFineTheBuyerTheNextDay()
    {
        GameData data = TestData.Load();
        data.Fx.Difficulties["argentina"].RaidChance = 1; // siempre hay operativo
        EconomySimulation sim = NewGame("argentina", data: data);
        sim.RecordBluePurchase(100, 10_000);
        DayStartedInfo info = sim.OnDayStarted(Monday.AddDays(1));
        Assert.Contains(info.Legal.Notices, n => n.Kind == ZuzuMarket.Core.Legal.LegalNoticeKind.BlueRaid && n.Amount == 5_000);
        Assert.Contains(sim.Taxes.Unpaid, o => o.Kind == TaxKind.DeudaVarios && o.Issued == 5_000);
        AssertBalanced(sim);
    }

    [Fact]
    public void OldSave_GetsGotoroBondsOnLoad_AndCanSwitchToArgentina()
    {
        EconomySimulation sim = NewGame("realista");
        WorldState state = sim.Snapshot();
        state.Markets.Bonds.RemoveAll(b => b.IsGotoro); // como una partida anterior a la Fase 9

        EconomySimulation loaded = EconomySimulation.Load(TestData.Load(), state);
        Assert.NotNull(loaded.Broker.MepBond);
        Assert.False(loaded.FxRules.Cepo);

        Assert.True(loaded.ChangeDifficulty("argentina"));
        EconomySimulation reloaded = EconomySimulation.Load(TestData.Load(), loaded.Snapshot());
        Assert.True(reloaded.FxRules.Cepo);
        Assert.True(reloaded.Fx.Gap > 0);
        Assert.Equal(200m, reloaded.Broker.QuotaLeft(Monday));
        Assert.False(loaded.ChangeDifficulty("imposible"));
    }

    [Fact]
    public void SaveAndLoad_KeepsRatesAndBalances()
    {
        EconomySimulation sim = NewGame("argentina", deposit: 100_000);
        sim.Broker.BuyOfficial(100, Monday, TenAm);
        sim.Broker.BuyMep(10_000, Monday, TenAm);
        EconomySimulation loaded = EconomySimulation.Load(TestData.Load(), WorldStateSerializer.Deserialize(WorldStateSerializer.Serialize(sim.Snapshot())));
        Assert.Equal(sim.Fx.Official, loaded.Fx.Official, 9);
        Assert.Equal(sim.Fx.Gap, loaded.Fx.Gap, 9);
        Assert.Equal(100m, loaded.Broker.Gtd);
        Assert.Single(loaded.Broker.State.MepOrders);
    }
}
