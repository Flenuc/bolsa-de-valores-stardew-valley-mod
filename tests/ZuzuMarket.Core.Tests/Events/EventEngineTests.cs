using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Events;
using ZuzuMarket.Core.Random;
using ZuzuMarket.Core.Simulation;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Tests.Events;

public class EventEngineTests
{
    private static readonly DifficultyDefinition Realista = TestData.Real.FindDifficulty("realista")!;

    private static EventDefinition Def(string id, double probability = 1.0, EventTone tone = EventTone.Neutral, int duration = 5, int cooldown = 0) => new()
    {
        Id = id,
        Name = id,
        Tone = tone,
        Probability = probability,
        DurationDays = duration,
        CooldownDays = cooldown,
        Effects = { new EventEffect { Target = "chirivia", PriceImpact = 0.2 } },
    };

    [Fact]
    public void Eligibility_BySeasonWeatherYearAndFixedDate()
    {
        var summerSun = Def("a");
        summerSun.Seasons.Add(Season.Summer);
        summerSun.Weather.Add(Weather.Sun);
        var fromYear2 = Def("b");
        fromYear2.MinYear = 2;
        var festival = Def("c");
        festival.FixedDate = new FixedEventDate { Season = Season.Spring, Day = 11 };

        var engine = new EventEngine(TestData.WithEvents(summerSun, fromYear2, festival), new EventsState());
        var summerDay = new EconomicDate(1, Season.Summer, 3);

        Assert.True(engine.IsEligible(summerSun, summerDay, Weather.Sun));
        Assert.False(engine.IsEligible(summerSun, summerDay, Weather.Rain));
        Assert.False(engine.IsEligible(summerSun, new EconomicDate(1, Season.Fall, 3), Weather.Sun));
        Assert.False(engine.IsEligible(fromYear2, summerDay, Weather.Sun));
        Assert.True(engine.IsEligible(fromYear2, new EconomicDate(2, Season.Spring, 1), Weather.Sun));
        Assert.True(engine.IsEligible(festival, new EconomicDate(3, Season.Spring, 11), Weather.Rain));
        Assert.False(engine.IsEligible(festival, new EconomicDate(3, Season.Spring, 12), Weather.Rain));
    }

    [Fact]
    public void AtMostTwoNewEventsPerDay()
    {
        var engine = new EventEngine(TestData.WithEvents(Def("a"), Def("b"), Def("c"), Def("d")), new EventsState());
        IReadOnlyList<EventDefinition> started = engine.Roll(EconomicDate.Start, Weather.Sun, Realista, new DeterministicRandom(1));
        Assert.Equal(EventEngine.MaxNewEventsPerDay, started.Count);
        Assert.Equal(2, engine.Active.Count);
    }

    [Fact]
    public void ActiveEvents_DoNotRestartAndCooldownIsRespected()
    {
        EventDefinition e = Def("a", duration: 3, cooldown: 5);
        var engine = new EventEngine(TestData.WithEvents(e), new EventsState());
        var day = EconomicDate.Start;

        Assert.Single(engine.Roll(day, Weather.Sun, Realista, new DeterministicRandom(1)));
        Assert.Empty(engine.Roll(day.AddDays(1), Weather.Sun, Realista, new DeterministicRandom(2)));

        // Termina el día 3 (inclusive); el cooldown de 5 días deja volver a empezar recién el día 9.
        engine.Expire(day.AddDays(3));
        Assert.Empty(engine.Active);
        Assert.False(engine.IsEligible(e, day.AddDays(7), Weather.Sun));
        Assert.True(engine.IsEligible(e, day.AddDays(8), Weather.Sun));
    }

    [Fact]
    public void NegativeEvents_ScaleWithDifficulty()
    {
        int Count(string difficultyId)
        {
            DifficultyDefinition d = TestData.Real.FindDifficulty(difficultyId)!;
            int started = 0;
            for (ulong seed = 0; seed < 2000; seed++)
            {
                var engine = new EventEngine(TestData.WithEvents(Def("mala", 0.4, EventTone.Negative)), new EventsState());
                started += engine.Roll(EconomicDate.Start, Weather.Sun, d, new DeterministicRandom(seed)).Count;
            }

            return started;
        }

        Assert.InRange(Count("casual"), 300, 500);     // 0.4 × 0.5 = 20%
        Assert.InRange(Count("realista"), 700, 900);   // 40%
        Assert.InRange(Count("argentina"), 1300, 1500); // 0.4 × 1.75 = 70%
    }

    [Fact]
    public void DecayProfile_FadesLinearly()
    {
        EventDefinition e = Def("a", duration: 4);
        e.Effects[0].Profile = ImpactProfile.Decay;
        GameData data = TestData.WithEvents(e);
        var engine = new EventEngine(data, new EventsState());
        CommodityDefinition parsnip = data.FindCommodity("chirivia")!;
        engine.Start(e, EconomicDate.Start);

        Assert.Equal(0.2, engine.PriceImpact(parsnip, EconomicDate.Start), 10);
        Assert.Equal(0.1, engine.PriceImpact(parsnip, EconomicDate.Start.AddDays(2)), 10);
        Assert.Equal(0.0, engine.PriceImpact(parsnip, EconomicDate.Start.AddDays(4)), 10);
        Assert.Equal(0.0, engine.PriceImpact(data.FindCommodity("papa")!, EconomicDate.Start), 10);
    }

    [Fact]
    public void Targets_MatchCommoditySectorAndAll()
    {
        CommodityDefinition wheat = TestData.Real.FindCommodity("trigo")!;
        Assert.True(new EventEffect { Target = "trigo" }.Matches(wheat));
        Assert.True(new EventEffect { Target = "sector:granos" }.Matches(wheat));
        Assert.True(new EventEffect { Target = "all" }.Matches(wheat));
        Assert.False(new EventEffect { Target = "sector:Mineria" }.Matches(wheat));
    }

    [Fact]
    public void RealEvents_HaveAReasonableFrequency()
    {
        EconomySimulation sim = EconomySimulation.StartNewGame(TestData.Load(), 99, EconomicDate.Start, "realista", 0);
        int total = 0;
        const int years = 5;
        while (sim.CurrentDate < new EconomicDate(years + 1, Season.Spring, 1))
        {
            Weather weather = sim.Random("clima-test").NextBool(0.3) ? Weather.Rain : Weather.Sun;
            total += sim.OnDayStarted(sim.CurrentDate.AddDays(1), weather).NewEvents.Count;
        }

        double perSeason = total / (years * 4.0);
        Assert.InRange(perSeason, 1.0, 6.0);
    }

    [Fact]
    public void ForcedEvent_MovesPricesImmediately()
    {
        EconomySimulation sim = EconomySimulation.StartNewGame(TestData.Load(), 5, new EconomicDate(1, Season.Summer, 1), "realista", 0);
        double before = sim.Market.Get("maiz").RealMultiplier;
        Assert.NotNull(sim.ForceEvent("langostas"));
        Assert.Equal(before * Math.Exp(0.30), sim.Market.Get("maiz").RealMultiplier, 6);
        Assert.Contains(sim.Events.State.News, n => n.Kind == NewsKind.Event && n.Key == "langostas");
        Assert.Null(sim.ForceEvent("no-existe"));
    }

    [Fact]
    public void ForcedEmission_RaisesInflation()
    {
        EconomySimulation sim = EconomySimulation.StartNewGame(TestData.Load(), 5, EconomicDate.Start, "casual", 0);
        double before = sim.Inflation.PeriodRate;
        sim.ForceEvent("emision");
        Assert.Equal(before + 0.02, sim.Inflation.PeriodRate, 10);
    }
}
