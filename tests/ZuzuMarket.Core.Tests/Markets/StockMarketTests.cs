using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Markets;
using ZuzuMarket.Core.Simulation;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Tests.Markets;

public class StockMarketTests
{
    private static readonly EconomicDate Monday = new(1, Season.Spring, 1);

    /// <summary>Sin ruido en commodities ni acciones y sin inflación (con los eventos reales, para poder forzarlos).</summary>
    private static GameData Quiet()
    {
        GameData data = TestData.Load();
        foreach (DifficultyDefinition d in data.Difficulties)
        {
            d.InflationMin = 0;
            d.InflationMax = 0;
        }

        foreach (CommodityDefinition c in data.Commodities)
        {
            c.Market.Volatility = 0;
        }

        foreach (CompanyDefinition c in data.Companies)
        {
            c.Volatility = 0;
        }

        return data;
    }

    [Fact]
    public void Start_PricesAtFundamentalValueAndMerzuAtBase()
    {
        EconomySimulation sim = EconomySimulation.StartNewGame(TestData.Real, 1, Monday, "realista", 0);
        Assert.True(sim.Stocks.Started);
        Assert.Equal(10, sim.Stocks.Companies.Count);
        Assert.Equal(1000, sim.Stocks.Merzu);

        CompanyDefinition zuen = sim.Stocks.Find("ZUEN")!; // sin exposiciones: precio = P/E × UPA
        Assert.Equal(zuen.Pe * zuen.BaseEps, sim.Stocks.Get("ZUEN").Close, 6);
        Assert.Single(sim.Stocks.Get("ZUEN").History);
        Assert.All(sim.Stocks.Get("ZUEN").Intraday, p => Assert.Equal(sim.Stocks.Get("ZUEN").Close, p, 6));
    }

    [Fact]
    public void ThreeYears_PricesStayReasonableAndOnlyMoveOnBusinessDays()
    {
        EconomySimulation sim = EconomySimulation.StartNewGame(TestData.WithoutEvents(), 2026, Monday, "realista", 0);
        TestData.AdvanceTo(sim, new EconomicDate(4, Season.Spring, 1));

        int businessDays = Enumerable.Range(1, 3 * EconomicDate.DaysPerYear).Count(d => Monday.AddDays(d).IsBusinessDay);
        foreach (CompanyDefinition c in sim.Stocks.Companies)
        {
            List<StockClose> history = sim.Stocks.Get(c.Ticker).History;
            Assert.Equal(businessDays + 1, history.Count);
            Assert.All(history, h => Assert.True(h.Date == Monday || h.Date.IsBusinessDay));
            double fundamental = c.Pe * c.BaseEps * sim.Inflation.Index;
            Assert.All(history.Skip(1), h => Assert.InRange(h.Close / (c.Pe * c.BaseEps), 0.3, 6.0));
            Assert.InRange(sim.Stocks.Get(c.Ticker).Close / fundamental, 0.3, 3.0);
        }

        Assert.Equal(businessDays + 1, sim.Stocks.State.MerzuHistory.Count);
    }

    [Fact]
    public void Weekend_PricesDoNotMove()
    {
        EconomySimulation sim = EconomySimulation.StartNewGame(TestData.Real, 3, Monday, "realista", 0);
        TestData.AdvanceTo(sim, new EconomicDate(1, Season.Spring, 5)); // viernes
        double friday = sim.Stocks.Get("BZUZ").Close;
        int count = sim.Stocks.Get("BZUZ").History.Count;

        TestData.AdvanceTo(sim, new EconomicDate(1, Season.Spring, 7)); // domingo
        Assert.Equal(friday, sim.Stocks.Get("BZUZ").Close);
        Assert.Equal(count, sim.Stocks.Get("BZUZ").History.Count);
        Assert.Equal(0, sim.Stocks.Get("BZUZ").DailyChange);
    }

    [Fact]
    public void CommodityShocks_MoveExposedCompanies()
    {
        EconomySimulation control = EconomySimulation.StartNewGame(Quiet(), 9, Monday, "realista", 0);
        EconomySimulation shocked = EconomySimulation.StartNewGame(Quiet(), 9, Monday, "realista", 0);
        shocked.ForceEvent("langostas");    // trigo y maíz suben
        shocked.ForceEvent("huelga-minera"); // minerales suben

        control.OnDayStarted(Monday.AddDays(1));
        shocked.OnDayStarted(Monday.AddDays(1));

        double Ratio(string ticker) => shocked.Stocks.Get(ticker).Close / control.Stocks.Get(ticker).Close;
        Assert.True(Ratio("COAV") > 1.01, "COAV debería subir con los granos");
        Assert.True(Ratio("GOTR") > 1.05, "GOTR debería subir con granos y minerales");
        Assert.True(Ratio("CALI") > 1.2, "CALI debería subir con la huelga minera");
        Assert.True(Ratio("DEST") < 0.99, "DEST debería bajar: trigo y maíz son su costo");
        Assert.Equal(1.0, Ratio("ZUEN"), 9);
    }

    [Fact]
    public void IntradayPath_IsDeterministicAndEndsAtTheClose()
    {
        EconomySimulation a = EconomySimulation.StartNewGame(TestData.Real, 77, Monday, "argentina", 0);
        EconomySimulation b = EconomySimulation.StartNewGame(TestData.Real, 77, Monday, "argentina", 0);
        a.OnDayStarted(Monday.AddDays(1));
        b.OnDayStarted(Monday.AddDays(1));

        StockState s = a.Stocks.Get("CALI");
        Assert.Equal(48, s.Intraday.Count);
        Assert.Equal(s.Close, s.Intraday[^1]);
        Assert.Equal(s.Intraday, b.Stocks.Get("CALI").Intraday);
        Assert.Equal(s.PreviousClose, a.Stocks.PriceAt("CALI", 600));
        Assert.Equal(s.PreviousClose, a.Stocks.PriceAt("CALI", 900));
        Assert.Equal(s.Intraday[0], a.Stocks.PriceAt("CALI", 910));
        Assert.Equal(s.Intraday[26], a.Stocks.PriceAt("CALI", 1330));
        Assert.Equal(s.Close, a.Stocks.PriceAt("CALI", 1700));
        Assert.Equal(s.Close, a.Stocks.PriceAt("CALI", 2200));
    }

    [Fact]
    public void Merzu_IsWeightedByCapitalization()
    {
        EconomySimulation sim = EconomySimulation.StartNewGame(TestData.Real, 5, Monday, "realista", 0);
        double baseCap = sim.Stocks.Companies.Sum(c => sim.Stocks.Get(c.Ticker).Close * c.Shares);
        TestData.AdvanceTo(sim, new EconomicDate(1, Season.Spring, 12));
        double cap = sim.Stocks.Companies.Sum(c => sim.Stocks.Get(c.Ticker).Close * c.Shares);
        Assert.Equal(1000 * cap / baseCap, sim.Stocks.Merzu, 6);
        Assert.Equal(sim.Stocks.Merzu, sim.Stocks.MerzuAt(1700), 6);
    }
}
