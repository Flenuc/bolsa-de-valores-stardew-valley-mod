using System.Text.Json;
using System.Text.RegularExpressions;
using ZuzuMarket.Core.Companies;
using ZuzuMarket.Core.Simulation;
using ZuzuMarket.Core.Taxation;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Tests.Balance;

/// <summary>Chequeos de consistencia de la versión 1.0: arbitrajes y traducciones.</summary>
public class ConsistencyTests
{
    [Theory]
    [InlineData("casual")]
    [InlineData("realista")]
    [InlineData("argentina")]
    public void IssuingBondsToPlaceInCauciones_IsNotFreeMoney(string difficulty)
    {
        var date = new EconomicDate(1, Season.Spring, 1);
        EconomySimulation sim = EconomySimulation.StartNewGame(TestData.Load(), 3, date, difficulty, 1_000_000);
        sim.OnDayStarted(date);
        CompanyState sa = sim.Companies.Create("minera", "SA", 50_000, true, 1_000_000, date, new List<CompanyNotice>()).Company!;

        // El cupón de la ON propia (por estación) siempre paga más que la caución colocadora en el mismo plazo.
        double caucionPerSeason = sim.Broker.CaucionRate;
        foreach (int term in sim.Data.PlayerCompanies.CapitalMarket.BondTerms)
        {
            Assert.True(sim.Companies.BondCoupon(sa, term) > caucionPerSeason, $"{difficulty}: cupón a {term} estaciones vs caución {caucionPerSeason:P2}");
        }
    }

    [Fact]
    public void Translations_HaveTheSameKeysAndTokens()
    {
        Dictionary<string, string> es = Load("es.json");
        Dictionary<string, string> en = Load("default.json");
        Assert.Empty(es.Keys.Except(en.Keys).OrderBy(k => k));
        Assert.Empty(en.Keys.Except(es.Keys).OrderBy(k => k));

        var tokens = new Regex(@"\{\{(\w+)\}\}");
        foreach (string key in es.Keys)
        {
            var a = tokens.Matches(es[key]).Select(m => m.Groups[1].Value).ToHashSet();
            var b = tokens.Matches(en[key]).Select(m => m.Groups[1].Value).ToHashSet();
            Assert.True(a.SetEquals(b), $"{key}: tokens distintos ({string.Join(",", a)} / {string.Join(",", b)})");
        }
    }

    private static Dictionary<string, string> Load(string file)
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "i18n", file));
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using JsonDocument doc = JsonDocument.Parse(json, options);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
    }
}