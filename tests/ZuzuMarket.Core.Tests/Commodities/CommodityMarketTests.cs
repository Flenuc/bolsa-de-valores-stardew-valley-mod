using ZuzuMarket.Core.Commodities;
using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Simulation;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Tests.Commodities;

public class CommodityMarketTests
{
    private const string Parsnip = "24";

    [Fact]
    public void ThreeYearsWithoutSales_StayWithinBoundsAndAroundTheSeasonalLevel()
    {
        GameData data = TestData.WithoutEvents();
        EconomySimulation sim = EconomySimulation.StartNewGame(data, 2026, EconomicDate.Start, "realista", 0);
        TestData.AdvanceTo(sim, new EconomicDate(4, Season.Spring, 1));

        foreach (CommodityDefinition c in data.Commodities)
        {
            List<PricePoint> history = sim.Market.Get(c.Id).History;
            Assert.Equal(3 * EconomicDate.DaysPerYear, history.Count);
            Assert.All(history, p => Assert.InRange(p.RealMultiplier, CommodityMarket.MinRealMultiplier, CommodityMarket.MaxRealMultiplier));

            foreach (Season season in Enum.GetValues<Season>())
            {
                double[] values = history.Where(p => p.Date.Season == season).Select(p => p.RealMultiplier).OrderBy(v => v).ToArray();
                double median = values[values.Length / 2];
                double expected = c.SeasonFactor(season);
                Assert.InRange(median / expected, 0.85, 1.15);
            }
        }
    }

    [Fact]
    public void SellingInBulk_SinksThePriceAndItRecovers()
    {
        GameData data = TestData.WithoutEvents();
        EconomySimulation quiet = EconomySimulation.StartNewGame(data, 7, EconomicDate.Start, "realista", 0);
        EconomySimulation seller = EconomySimulation.StartNewGame(data, 7, EconomicDate.Start, "realista", 0);
        var saleDay = new EconomicDate(1, Season.Spring, 5);
        TestData.AdvanceTo(quiet, saleDay);
        TestData.AdvanceTo(seller, saleDay);

        Assert.True(seller.RecordShipment(Parsnip, 2000));
        quiet.OnDayStarted(saleDay.AddDays(1));
        seller.OnDayStarted(saleDay.AddDays(1));

        double Ratio() => seller.Market.Get("chirivia").Multiplier / quiet.Market.Get("chirivia").Multiplier;
        Assert.InRange(Ratio(), 0.60, 0.75);
        Assert.Equal(2000, seller.Market.Get("chirivia").History[^1].UnitsSold);

        // Los demás commodities no se ven afectados (mismo ruido, misma semilla).
        Assert.Equal(quiet.Market.Get("papa").Multiplier, seller.Market.Get("papa").Multiplier);

        TestData.AdvanceTo(quiet, saleDay.AddDays(21));
        TestData.AdvanceTo(seller, saleDay.AddDays(21));
        Assert.True(Ratio() > 0.90, $"Recuperación insuficiente: {Ratio():P1}");
    }

    [Fact]
    public void SmallSales_MoveThePriceALittle()
    {
        GameData data = TestData.Deterministic();
        EconomySimulation sim = EconomySimulation.StartNewGame(data, 1, EconomicDate.Start, "realista", 0);
        double before = sim.Market.Get("chirivia").RealMultiplier;
        sim.RecordShipment(Parsnip, 100);
        sim.OnDayStarted(sim.CurrentDate.AddDays(1));
        double drop = 1 - (sim.Market.Get("chirivia").RealMultiplier / before);
        Assert.InRange(drop, 0.02, 0.08);
    }

    [Fact]
    public void OffSeason_IsMoreExpensiveThanHarvestSeason()
    {
        GameData data = TestData.Deterministic();
        EconomySimulation sim = EconomySimulation.StartNewGame(data, 1, EconomicDate.Start, "casual", 0);
        Assert.Equal(0.95, sim.Market.Get("chirivia").RealMultiplier, 6);
        Assert.Equal(1.0, sim.Market.Get("leche").RealMultiplier, 6);

        TestData.AdvanceTo(sim, new EconomicDate(1, Season.Summer, 1));
        Assert.Equal(1.30, sim.Market.Get("chirivia").RealMultiplier, 6);
        Assert.Equal(0.95, sim.Market.Get("tomate").RealMultiplier, 6);
    }

    [Fact]
    public void Multiplier_IncludesInflationAndAppliesToNonCommodities()
    {
        GameData data = TestData.Deterministic();
        EconomySimulation sim = EconomySimulation.StartNewGame(data, 3, EconomicDate.Start, "argentina", 0);
        TestData.AdvanceTo(sim, new EconomicDate(1, Season.Spring, 20));

        double ipc = sim.Inflation.Index;
        Assert.True(ipc > 1.0);
        Assert.Equal(sim.Market.Get("leche").RealMultiplier * ipc, sim.Market.GetMultiplierForItem("186"), 10);
        Assert.Equal(ipc, sim.Market.GetMultiplierForItem("388"), 10); // Madera: no es commodity.
        Assert.False(sim.RecordShipment("388", 10));
        Assert.False(sim.RecordShipment(Parsnip, 0));
    }

    [Fact]
    public void NewCommoditiesInData_GetAStateAutomatically()
    {
        var states = new Dictionary<string, CommodityState>();
        var market = new CommodityMarket(TestData.Real, states);
        Assert.Equal(15, states.Count);
        Assert.Equal(1.0, market.Get("vino").Multiplier);
    }
}
