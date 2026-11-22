using ZuzuMarket.Core.Markets;
using ZuzuMarket.Core.Simulation;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Tests.Markets;

public class CommissionDiscountTests
{
    [Fact]
    public void FriendshipDiscount_LowersTheBrokerCommission()
    {
        var monday = new EconomicDate(1, Season.Spring, 1);
        EconomySimulation sim = EconomySimulation.StartNewGame(TestData.WithoutInflation(), 1, monday, "realista", 1_000_000);
        sim.OnDayStarted(monday);
        sim.Broker.Open(monday);
        sim.Broker.Deposit(500_000, monday);

        TradeQuote full = sim.Broker.Quote("JOJA", TradeSide.Buy, 1_000, monday, 1000);
        sim.Broker.CommissionDiscount = () => 0.40m;
        TradeQuote friend = sim.Broker.Quote("JOJA", TradeSide.Buy, 1_000, monday, 1000);

        Assert.Equal(Math.Round(full.Commission * 0.6m, 2), friend.Commission);
        Assert.True(friend.Total < full.Total);
    }
}