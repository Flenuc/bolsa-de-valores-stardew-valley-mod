using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Macro;
using ZuzuMarket.Core.Random;
using ZuzuMarket.Core.Simulation;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Tests.Macro;

public class InflationTests
{
    [Theory]
    [InlineData("casual")]
    [InlineData("realista")]
    [InlineData("argentina")]
    public void PeriodRate_IsWithinDifficultyRange(string difficultyId)
    {
        DifficultyDefinition difficulty = TestData.Real.FindDifficulty(difficultyId)!;
        for (ulong seed = 0; seed < 200; seed++)
        {
            var model = new InflationModel(new InflationState());
            model.StartPeriod(new FiscalPeriod(1, Season.Spring), difficulty, new DeterministicRandom(seed));
            Assert.InRange(model.PeriodRate, (double)difficulty.InflationMin, (double)difficulty.InflationMax);
        }
    }

    [Fact]
    public void Index_CompoundsTheMonthlyRateOverTheSeason()
    {
        var state = new InflationState { CurrentPeriod = new FiscalPeriod(1, Season.Spring), PeriodRate = 0.04 };
        var model = new InflationModel(state);
        for (int i = 0; i < EconomicDate.DaysPerSeason; i++)
        {
            model.AdvanceDay();
        }

        Assert.Equal(1.04, model.Index, 10);
        InflationRecord record = model.ClosePeriod()!;
        Assert.Equal(0.04, record.Rate, 10);
        Assert.Single(state.History);
    }

    [Fact]
    public void Shock_AddsToTheCurrentRate()
    {
        var model = new InflationModel(new InflationState { CurrentPeriod = new FiscalPeriod(1, Season.Spring), PeriodRate = 0.03 });
        model.AddShock(0.02);
        Assert.Equal(0.05, model.PeriodRate, 10);
    }

    [Theory]
    [InlineData("casual")]
    [InlineData("realista")]
    [InlineData("argentina")]
    public void OneYear_IndexStaysWithinCompoundedBounds(string difficultyId)
    {
        GameData data = TestData.WithoutEvents();
        DifficultyDefinition d = data.FindDifficulty(difficultyId)!;
        EconomySimulation sim = EconomySimulation.StartNewGame(data, 42, EconomicDate.Start, difficultyId, 0);

        var published = new List<InflationRecord>();
        while (sim.CurrentDate < new EconomicDate(2, Season.Spring, 1))
        {
            published.AddRange(sim.OnDayStarted(sim.CurrentDate.AddDays(1)).PublishedInflation);
        }

        // El primer día (primavera 1) no tiene inflación propia: el índice arranca en 1 ese día.
        double lower = Math.Pow(1 + (double)d.InflationMin, 4 - (1.0 / 28)) - 1e-9;
        double upper = Math.Pow(1 + (double)d.InflationMax, 4) + 1e-9;
        Assert.InRange(sim.Inflation.Index, lower, upper);
        Assert.Equal(4, published.Count);
        Assert.Equal(new FiscalPeriod(1, Season.Winter), published[^1].Period);
    }
}
