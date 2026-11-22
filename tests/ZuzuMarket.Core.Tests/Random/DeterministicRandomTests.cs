using ZuzuMarket.Core.Random;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Tests.Random;

public class DeterministicRandomTests
{
    [Fact]
    public void Xoshiro256StarStar_MatchesReferenceVector()
    {
        // Vector de referencia de la implementación en C de Blackman & Vigna con estado {1, 2, 3, 4}.
        var rng = new DeterministicRandom(1, 2, 3, 4);
        Assert.Equal(11520UL, rng.NextUInt64());
        Assert.Equal(0UL, rng.NextUInt64());
        Assert.Equal(1509978240UL, rng.NextUInt64());
        Assert.Equal(1215971899390074240UL, rng.NextUInt64());
    }

    [Fact]
    public void SplitMix64_MatchesReferenceVector()
    {
        ulong state = 1234567;
        Assert.Equal(6457827717110365317UL, SplitMix64.Next(ref state));
        Assert.Equal(3203168211198807973UL, SplitMix64.Next(ref state));
        Assert.Equal(9817491932198370423UL, SplitMix64.Next(ref state));
    }

    [Fact]
    public void Fnv1a_MatchesReferenceVector()
    {
        Assert.Equal(14695981039346656037UL, StableHash.Fnv1a64(""));
        Assert.Equal(0xAF63DC4C8601EC8CUL, StableHash.Fnv1a64("a"));
    }

    [Fact]
    public void SameSeed_SameSequence()
    {
        var a = new DeterministicRandom(42);
        var b = new DeterministicRandom(42);
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(a.NextUInt64(), b.NextUInt64());
        }
    }

    [Fact]
    public void Streams_AreReproducibleAndIndependent()
    {
        var date = new EconomicDate(1, Season.Summer, 10);
        double[] Take(DeterministicRandom r) => Enumerable.Range(0, 20).Select(_ => r.NextDouble()).ToArray();

        double[] events1 = Take(RandomStreams.For(123, date, "eventos"));
        double[] events2 = Take(RandomStreams.For(123, date, "eventos"));
        double[] prices = Take(RandomStreams.For(123, date, "precios"));
        double[] nextDay = Take(RandomStreams.For(123, date.AddDays(1), "eventos"));
        double[] otherSave = Take(RandomStreams.For(124, date, "eventos"));
        double[] wholeGame = Take(RandomStreams.ForGame(123, "eventos"));

        Assert.Equal(events1, events2);
        Assert.NotEqual(events1, prices);
        Assert.NotEqual(events1, nextDay);
        Assert.NotEqual(events1, otherSave);
        Assert.NotEqual(events1, wholeGame);
    }

    [Fact]
    public void NextDouble_IsInUnitInterval()
    {
        var rng = new DeterministicRandom(7);
        for (int i = 0; i < 10_000; i++)
        {
            double value = rng.NextDouble();
            Assert.InRange(value, 0.0, 0.9999999999999999);
        }
    }

    [Fact]
    public void NextInt_StaysInRangeAndCoversIt()
    {
        var rng = new DeterministicRandom(99);
        var seen = new HashSet<int>();
        for (int i = 0; i < 5_000; i++)
        {
            int value = rng.NextInt(-3, 4);
            Assert.InRange(value, -3, 3);
            seen.Add(value);
        }

        Assert.Equal(7, seen.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(5, 5));
    }

    [Fact]
    public void NextGaussian_HasRoughlyStandardMoments()
    {
        var rng = new DeterministicRandom(2026);
        const int n = 50_000;
        double[] values = Enumerable.Range(0, n).Select(_ => rng.NextGaussian()).ToArray();
        double mean = values.Average();
        double variance = values.Select(v => (v - mean) * (v - mean)).Sum() / (n - 1);
        Assert.InRange(mean, -0.03, 0.03);
        Assert.InRange(variance, 0.97, 1.03);
    }

    [Fact]
    public void EmptyStreamName_Throws()
    {
        Assert.Throws<ArgumentException>(() => RandomStreams.For(1, EconomicDate.Start, " "));
    }
}
