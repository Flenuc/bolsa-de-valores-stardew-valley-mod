using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Ledger;
using ZuzuMarket.Core.Macro;
using ZuzuMarket.Core.Markets;
using ZuzuMarket.Core.Money;
using ZuzuMarket.Core.Simulation;
using ZuzuMarket.Core.State;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Tests.Markets;

public class FixedIncomeTests
{
    private const int TenAm = 1000;
    private static readonly EconomicDate Monday = new(1, Season.Spring, 1);

    private static EconomySimulation NewAccount(GameData? data = null, string difficulty = "realista", decimal deposit = 50_000)
    {
        EconomySimulation sim = EconomySimulation.StartNewGame(data ?? TestData.WithoutInflation(), 1, Monday, difficulty, 100_000);
        sim.Broker.Open(Monday);
        sim.Broker.Deposit(deposit, Monday);
        return sim;
    }

    private static decimal Balance(EconomySimulation sim, string account) => sim.Ledger.GetBalance(LedgerEntities.Jugador, account);

    private static void AssertBooks(EconomySimulation sim)
    {
        IReadOnlyList<TrialBalanceRow> rows = sim.Ledger.GetTrialBalance(LedgerEntities.Jugador);
        Assert.Equal(rows.Sum(r => r.Debits), rows.Sum(r => r.Credits));
        Assert.Equal(sim.Broker.State.Bonds.Values.Sum(h => h.CostBasis), Balance(sim, SystemAccounts.Bonos));
    }

    // ───────────────────────────── BCRF y curva ─────────────────────────────

    [Fact]
    public void CentralBank_FollowsTheTaylorRuleWithFloorAndSmoothing()
    {
        DifficultyDefinition realista = TestData.Real.FindDifficulty("realista")!; // meta 2%
        var rules = new CentralBankRules { NeutralRealRate = 0.004, Phi = 0.5, Floor = 0.002, MaxStep = 0.02 };
        var bank = new CentralBank(new CentralBankState(), rules);

        bank.Start(Monday, 0.03, realista);
        Assert.Equal(0.03 + 0.004 + (0.5 * 0.01), bank.PolicyRate, 10);

        // Inflación muy alta: la regla pide 25,4% pero solo sube 2 puntos por reunión.
        RateDecision hike = bank.Meet(Monday.AddDays(28), 0.15, realista);
        Assert.Equal(0.02, hike.Change, 10);

        // Deflación: no baja del piso.
        for (int i = 0; i < 10; i++)
        {
            bank.Meet(Monday.AddDays(28 * (i + 2)), -0.05, realista);
        }

        Assert.Equal(0.002, bank.PolicyRate, 10);
        Assert.Equal(12, bank.State.History.Count);
    }

    [Fact]
    public void MoneyPrinting_ForcesAnEmergencyHike()
    {
        EconomySimulation sim = NewAccount(TestData.Load());
        double before = sim.CentralBank.PolicyRate;
        sim.ForceEvent("emision");
        Assert.Equal(before + 0.01, sim.CentralBank.PolicyRate, 10);
        Assert.Equal(sim.CentralBank.PolicyRate, sim.Broker.ReferenceRate, 10);
        Assert.Contains(sim.Events.State.News, n => n.Kind == ZuzuMarket.Core.Events.NewsKind.RateDecision && n.Key == "up");
    }

    [Fact]
    public void Curve_RisesWithTenorAndWorseRatings()
    {
        EconomySimulation sim = NewAccount();
        Broker b = sim.Broker;
        Assert.True(b.Yield(28, "JOJA") < b.Yield(448, "JOJA"));
        Assert.True(b.Yield(224, "JOJA") < b.Yield(224, "CALI")); // A < BB
        Assert.True(b.Yield(224, CreditEngine.Sovereign) > b.Yield(224, "JOJA")); // soberano B en Realista
        Assert.Equal(0.015 * 4 * 10_000, b.CountryRiskBps, 6);
        Assert.Equal(b.ReferenceRate * 0.85, b.CaucionRate, 10);
    }

    [Fact]
    public void SovereignRating_DependsOnDifficulty()
    {
        Assert.Equal("AA", NewAccount(difficulty: "casual").Broker.Rating(CreditEngine.Sovereign));
        Assert.Equal("B", NewAccount(difficulty: "realista").Broker.Rating(CreditEngine.Sovereign));
        Assert.Equal("CCC", NewAccount(difficulty: "argentina").Broker.Rating(CreditEngine.Sovereign));
    }

    // ───────────────────────────── Bonos ─────────────────────────────

    [Fact]
    public void Bonds_AreIssuedNearParAtStart()
    {
        EconomySimulation sim = NewAccount();
        Assert.Equal(new[] { "BOCER1", "BOFE1", "BOVA1" }, sim.Broker.ActiveBonds(sovereign: true).Select(b => b.Id).OrderBy(x => x));
        Assert.Equal(5, sim.Broker.ActiveBonds(sovereign: false).Count);
        foreach (BondSeries bond in sim.Broker.ActiveBonds(true).Concat(sim.Broker.ActiveBonds(false)))
        {
            double price = sim.Broker.BondPrice(bond, Monday);
            Assert.InRange(price, 80, 120);
            double yield = sim.Broker.BondYield(bond, Monday);
            Assert.Equal(price, BondMath.Price(bond, Monday, sim.Broker.Context(bond.Issuer) with { NominalYield = _ => bond.Type == BondType.Cer ? yield + sim.Inflation.PeriodRate : yield }), 3);
        }
    }

    [Fact]
    public void Price_IsThePresentValueOfTheFlows()
    {
        var bond = new BondSeries { Id = "T", Issuer = "X", Type = BondType.Fixed, IssueDate = Monday, TenorSeasons = 2, CouponRate = 0.03 };
        var ctx = new BondContext(0.02, 0, 1, _ => 0.02);
        // Cupón 3 en verano 1 (28 días) y 103 en otoño 1 (56 días), al 2% mensual.
        double expected = (3 / 1.02) + (103 / (1.02 * 1.02));
        Assert.Equal(expected, BondMath.Price(bond, Monday, ctx), 9);
        Assert.Equal(0.02, BondMath.Yield(bond, Monday, ctx, expected), 6);
        Assert.InRange(BondMath.Duration(bond, Monday, ctx), 1.9, 2.0);
    }

    [Fact]
    public void RateHikes_HitFixedBondsMoreThanFloatingOnes()
    {
        EconomySimulation sim = NewAccount(TestData.Load());
        BondSeries fixedBond = sim.Broker.FindBond("BOFE1")!;
        BondSeries floating = sim.Broker.FindBond("BOVA1")!;
        double fixedBefore = sim.Broker.BondPrice(fixedBond, Monday);
        double floatingBefore = sim.Broker.BondPrice(floating, Monday);

        sim.ForceEvent("emision"); // +1 punto de tasa
        double fixedDrop = 1 - (sim.Broker.BondPrice(fixedBond, Monday) / fixedBefore);
        double floatingDrop = 1 - (sim.Broker.BondPrice(floating, Monday) / floatingBefore);
        Assert.True(fixedDrop > 0.03, $"El bono fijo debería caer: {fixedDrop:P2}");
        Assert.True(floatingDrop < fixedDrop / 3, $"El variable debería caer mucho menos: {floatingDrop:P2}");
    }

    [Fact]
    public void CerBonds_FollowInflation()
    {
        EconomySimulation sim = NewAccount(TestData.WithoutEvents(), "argentina");
        BondSeries cer = sim.Broker.FindBond("BOCER1")!;
        double ipc0 = sim.Inflation.Index;
        IReadOnlyList<BondCashFlow> before = sim.Broker.BondFlows(cer);
        TestData.AdvanceTo(sim, new EconomicDate(1, Season.Spring, 27));
        IReadOnlyList<BondCashFlow> after = sim.Broker.BondFlows(cer);
        double ratio = sim.Inflation.Index / ipc0;
        Assert.True(ratio > 1.04);
        Assert.Equal(before[^1].Amortization * ratio, after[^1].Amortization, 6);
    }

    [Fact]
    public void CouponsAndAmortizations_ArePaidAndBooked()
    {
        EconomySimulation sim = NewAccount();
        TradeQuote buy = sim.Broker.Execute("ONCALI1", TradeSide.Buy, 20, Monday, TenAm);
        Assert.True(buy.IsValid);
        BondSeries cali = sim.Broker.FindBond("ONCALI1")!;
        double coupon = cali.CouponRate * 100 * 20;

        DayStartedInfo summer = sim.OnDayStarted(new EconomicDate(1, Season.Summer, 1));
        MarketNotice paid = Assert.Single(summer.MarketNotices, n => n.Kind == MarketNoticeKind.Coupon && n.Instrument == "ONCALI1");
        Assert.Equal(MoneyRounding.Round((decimal)coupon), paid.Amount);
        Assert.Equal(paid.Amount, Balance(sim, SystemAccounts.InteresesGanados));
        AssertBooks(sim);

        // Dos cuotas de amortización (estaciones 7 y 8): al final la tenencia desaparece y el costo queda en cero.
        decimal cash = sim.Broker.Cash;
        TestData.AdvanceTo(sim, new EconomicDate(3, Season.Spring, 1));
        Assert.False(sim.Broker.State.Bonds.ContainsKey("ONCALI1"));
        Assert.Null(sim.Broker.FindBond("ONCALI1"));
        Assert.Equal(0m, Balance(sim, SystemAccounts.Bonos));
        Assert.True(sim.Broker.Cash > cash + 2_000m - 1m); // recibió el nominal (2.000) más cupones
        AssertBooks(sim);
    }

    [Fact]
    public void Default_StopsPaymentsDropsThePriceAndEndsInAnExchange()
    {
        EconomySimulation sim = NewAccount();
        sim.Broker.Execute("ONJOJA1", TradeSide.Buy, 50, Monday, TenAm);
        sim.Broker.Execute("JOJA", TradeSide.Buy, 10, Monday, TenAm);
        decimal cost = sim.Broker.State.Bonds["ONJOJA1"].CostBasis;
        double stockBefore = sim.Stocks.Get("JOJA").Close;

        Assert.True(sim.ForceDefault("JOJA"));
        Assert.False(sim.ForceDefault("JOJA"));
        BondSeries bond = sim.Broker.FindBond("ONJOJA1")!;
        Assert.Equal(40, sim.Broker.BondPrice(bond, Monday), 6); // recupero 40%
        Assert.Equal("D", sim.Broker.Rating("JOJA"));
        Assert.True(sim.Stocks.PriceAt("JOJA", TenAm) < stockBefore * 0.3); // la acción se desploma en el momento

        sim.OnDayStarted(Monday.AddDays(1));
        Assert.True(sim.Stocks.Get("JOJA").Close < stockBefore * 0.3); // y sigue abajo

        DayStartedInfo summer = sim.OnDayStarted(new EconomicDate(1, Season.Summer, 1));
        Assert.DoesNotContain(summer.MarketNotices, n => n.Kind == MarketNoticeKind.Coupon && n.Instrument == "ONJOJA1");

        DayStartedInfo fall = sim.OnDayStarted(new EconomicDate(1, Season.Fall, 1));
        Assert.Contains(fall.MarketNotices, n => n.Kind == MarketNoticeKind.Exchange && n.Instrument == "ONJOJA1>ONJOJA1R" && n.Amount == 20);
        Assert.Contains(fall.CreditEvents, e => e.Kind == CreditEventKind.Restructuring && e.Issuer == "JOJA");
        Holding exchanged = sim.Broker.State.Bonds["ONJOJA1R"];
        Assert.Equal(20, exchanged.Quantity);
        Assert.Equal(cost, exchanged.CostBasis);
        Assert.Equal("B", sim.Broker.Rating("JOJA"));
        Assert.True(sim.Broker.FindBond("ONJOJA1R")!.IsRestructured);
        AssertBooks(sim);
    }

    [Fact]
    public void SovereignDefaults_DependOnDifficulty()
    {
        int Defaults(string difficulty)
        {
            int count = 0;
            for (ulong seed = 0; seed < 40; seed++)
            {
                EconomySimulation sim = EconomySimulation.StartNewGame(TestData.WithoutEvents(), seed, Monday, difficulty, 0);
                sim.OnDayStarted(new EconomicDate(6, Season.Spring, 1));
                count += sim.Events.State.News.Count(n => n.Kind == ZuzuMarket.Core.Events.NewsKind.Default && n.Key == CreditEngine.Sovereign);
            }

            return count;
        }

        Assert.Equal(0, Defaults("casual"));
        Assert.True(Defaults("argentina") > Defaults("realista"));
        Assert.True(Defaults("argentina") >= 20, "En Argentina el default soberano debería ser frecuente en 5 años");
    }

    [Fact]
    public void Ratings_MigrateWithinTheScale()
    {
        EconomySimulation sim = EconomySimulation.StartNewGame(TestData.WithoutEvents(), 3, Monday, "casual", 0);
        sim.OnDayStarted(new EconomicDate(11, Season.Spring, 1));
        RatingRules rules = sim.Data.Ratings;
        Assert.All(sim.Credit.Issuers.Values, s => Assert.InRange(rules.IndexOf(s.Rating), 0, rules.Scale.Count - 1));
        Assert.Contains(sim.Events.State.News, n => n.Kind == ZuzuMarket.Core.Events.NewsKind.RatingChange);
    }

    [Fact]
    public void AYearTradingBonds_KeepsTheBooksBalanced()
    {
        EconomySimulation sim = NewAccount(TestData.Load(), "argentina", 80_000);
        var rng = new ZuzuMarket.Core.Random.DeterministicRandom(5);
        for (int day = 0; day < EconomicDate.DaysPerYear * 2; day++)
        {
            EconomicDate today = sim.CurrentDate;
            var bonds = sim.Broker.ActiveBonds(true).Concat(sim.Broker.ActiveBonds(false)).ToList();
            if (bonds.Count > 0)
            {
                BondSeries bond = bonds[rng.NextInt(0, bonds.Count)];
                TradeSide side = rng.NextBool(0.6) ? TradeSide.Buy : TradeSide.Sell;
                long qty = rng.NextInt(1, 30);
                if (side == TradeSide.Sell && sim.Broker.State.Bonds.TryGetValue(bond.Id, out Holding? h))
                {
                    qty = Math.Min(qty, h.Quantity);
                }

                sim.Broker.Execute(bond.Id, side, qty, today, TenAm);
            }

            sim.OnDayStarted(today.AddDays(1));
            AssertBooks(sim);
        }

        Assert.True(Balance(sim, SystemAccounts.InteresesGanados) > 0);
    }

    [Fact]
    public void SaveAndLoad_KeepsBondsRatingsAndTheCentralBank()
    {
        EconomySimulation sim = NewAccount();
        sim.Broker.Execute("BOFE1", TradeSide.Buy, 10, Monday, TenAm);
        TestData.AdvanceTo(sim, new EconomicDate(1, Season.Summer, 5));

        EconomySimulation loaded = EconomySimulation.Load(TestData.WithoutInflation(), WorldStateSerializer.Deserialize(WorldStateSerializer.Serialize(sim.Snapshot())));
        Assert.Equal(sim.CentralBank.PolicyRate, loaded.CentralBank.PolicyRate);
        Assert.Equal(sim.Broker.Rating(CreditEngine.Sovereign), loaded.Broker.Rating(CreditEngine.Sovereign));
        BondSeries bond = loaded.Broker.FindBond("BOFE1")!;
        Assert.Equal(1, bond.PaymentsMade);
        Assert.Equal(sim.Broker.BondPrice(sim.Broker.FindBond("BOFE1")!, sim.CurrentDate), loaded.Broker.BondPrice(bond, loaded.CurrentDate));
        Assert.Equal(10, loaded.Broker.State.Bonds["BOFE1"].Quantity);
    }
}
