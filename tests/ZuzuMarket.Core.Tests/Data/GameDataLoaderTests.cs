using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Ledger;

namespace ZuzuMarket.Core.Tests.Data;

public class GameDataLoaderTests
{
    private static string Accounts => TestData.ReadFile(GameDataLoader.AccountsFile);

    private static string Commodities => TestData.ReadFile(GameDataLoader.CommoditiesFile);

    private static string Difficulties => TestData.ReadFile(GameDataLoader.DifficultiesFile);

    [Fact]
    public void RealData_LoadsWithoutErrors()
    {
        DataLoadResult result = GameDataLoader.LoadFromDirectory(TestData.DataDirectory);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void RealData_HasTheFifteenCommoditiesOfTheDesign()
    {
        GameData data = TestData.Real;
        Assert.Equal(15, data.Commodities.Count);
        Assert.Equal(
            new[] { "trigo", "maiz", "chirivia", "papa", "tomate", "arandano", "melon", "calabaza", "leche", "huevo", "cobre", "hierro", "oro", "pescado", "vino" },
            data.Commodities.Select(c => c.Id));
        Assert.Equal("pescado", data.FindCommodityByItemId("130")!.Id);
        Assert.Equal(CommodityPriceMode.Index, data.FindCommodityByItemId("348")!.PriceMode);
        Assert.Null(data.FindCommodityByItemId("388"));
    }

    [Fact]
    public void RealData_HasSystemAccountsAndDifficulties()
    {
        GameData data = TestData.Real;
        foreach (string code in SystemAccounts.All)
        {
            Assert.True(data.Chart.Contains(code), code);
        }

        Assert.Equal(AccountType.Activo, data.Chart.Get(SystemAccounts.Caja).Type);
        Assert.NotNull(data.FindDifficulty("Casual"));
        Assert.NotNull(data.FindDifficulty("realista"));
        Assert.True(data.FindDifficulty("argentina")!.CurrencyControls);
        Assert.False(data.FindDifficulty("casual")!.BankruptcyEnabled);
    }

    [Fact]
    public void DuplicateCommodityAndItem_AreReported()
    {
        const string commodities = """
            [
              { "id": "trigo", "name": "Trigo", "sector": "Granos", "itemIds": ["262"], "harvestSeasons": ["Summer"], "futuresContractSize": 500 },
              { "id": "trigo", "name": "Trigo 2", "sector": "Granos", "itemIds": ["262"], "harvestSeasons": ["Summer"], "futuresContractSize": 500 }
            ]
            """;

        DataLoadResult result = GameDataLoader.LoadFromJson(Accounts, commodities, Difficulties);
        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.Contains(result.Errors, e => e.Contains("duplicado trigo"));
        Assert.Contains(result.Errors, e => e.Contains("262"));
    }

    [Fact]
    public void InvalidCommodityFields_AreReported()
    {
        const string commodities = """
            [ { "id": "x", "name": "X", "sector": "", "itemIds": [], "futuresContractSize": 0 } ]
            """;

        DataLoadResult result = GameDataLoader.LoadFromJson(Accounts, commodities, Difficulties);
        Assert.Contains(result.Errors, e => e.Contains("no tiene sector"));
        Assert.Contains(result.Errors, e => e.Contains("no tiene objetos"));
        Assert.Contains(result.Errors, e => e.Contains("allYear"));
        Assert.Contains(result.Errors, e => e.Contains("futuresContractSize"));
    }

    [Fact]
    public void MissingSystemAccount_IsReported()
    {
        const string accounts = """[ { "code": "1.1.01", "name": "Caja", "type": "Activo" } ]""";
        DataLoadResult result = GameDataLoader.LoadFromJson(accounts, Commodities, Difficulties);
        Assert.Contains(result.Errors, e => e.Contains(SystemAccounts.Capital));
    }

    [Fact]
    public void MissingDifficulty_IsReported()
    {
        const string difficulties = """[ { "id": "casual", "name": "Casual" } ]""";
        DataLoadResult result = GameDataLoader.LoadFromJson(Accounts, Commodities, difficulties);
        Assert.Contains(result.Errors, e => e.Contains("realista"));
        Assert.Contains(result.Errors, e => e.Contains("argentina"));
    }

    [Fact]
    public void InvalidJson_IsReported()
    {
        DataLoadResult result = GameDataLoader.LoadFromJson("{ esto no es json", Commodities, Difficulties);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.StartsWith(GameDataLoader.AccountsFile));
    }

    [Fact]
    public void MissingDirectory_IsReported()
    {
        DataLoadResult result = GameDataLoader.LoadFromDirectory(Path.Combine(Path.GetTempPath(), "zuzu-no-existe"));
        Assert.Equal(9, result.Errors.Count);
    }

    [Fact]
    public void RealEvents_LoadAndPointToExistingTargets()
    {
        GameData data = TestData.Real;
        Assert.True(data.Events.Count >= 12);
        Assert.NotNull(data.FindEvent("sequia"));
        Assert.Equal(EventTone.Negative, data.FindEvent("emision")!.Tone);
        Assert.Equal(0.02, data.FindEvent("emision")!.InflationShock, 10);
    }

    [Fact]
    public void InvalidEvents_AreReported()
    {
        const string events = """
            [
              { "id": "a", "probability": 2, "durationDays": 0, "effects": [ { "target": "banana", "priceImpact": 0.1 } ] },
              { "id": "b", "probability": 0.1, "durationDays": 3, "effects": [ { "target": "sector:Espacial", "priceImpact": 0.1 } ] },
              { "id": "b", "probability": 0.1, "durationDays": 3, "effects": [] },
              { "id": "c", "probability": 0.1, "durationDays": 3, "fixedDate": { "season": "Spring", "day": 40 }, "effects": [ { "target": "all", "priceImpact": 3 } ] }
            ]
            """;

        DataLoadResult result = GameDataLoader.LoadFromJson(Accounts, Commodities, Difficulties, events);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("a tiene una probabilidad"));
        Assert.Contains(result.Errors, e => e.Contains("a necesita durationDays"));
        Assert.Contains(result.Errors, e => e.Contains("\"banana\""));
        Assert.Contains(result.Errors, e => e.Contains("\"sector:Espacial\""));
        Assert.Contains(result.Errors, e => e.Contains("duplicado b"));
        Assert.Contains(result.Errors, e => e.Contains("b no tiene efectos"));
        Assert.Contains(result.Errors, e => e.Contains("c tiene una fecha fija"));
        Assert.Contains(result.Errors, e => e.Contains("desmedido"));
    }

    [Fact]
    public void RealTaxes_Load()
    {
        var taxes = TestData.Real.Taxes;
        Assert.Equal(11, taxes.Monotributo.Categories.Count);
        Assert.Equal("K", taxes.Monotributo.Categories[^1].Id);
        Assert.Equal(0.105m, taxes.Iva.For("agro"));
        Assert.Equal(0.21m, taxes.Iva.For("mineria"));
        Assert.Equal("agro", taxes.ActivityFor(TestData.Real.FindCommodity("leche"), -6));
        Assert.Equal("pesca", taxes.ActivityFor(null, -4));
        Assert.Equal("otros", taxes.ActivityFor(null, -999));
        Assert.Equal("ManorHouse", taxes.PaymentDesk.Location);
    }

    [Fact]
    public void InvalidTaxes_AreReported()
    {
        const string taxes = """
            {
              "dueDay": 30,
              "iva": { "default": 1.5 },
              "monotributo": { "categories": [ { "id": "A", "annualCap": 100, "fee": 1 }, { "id": "B", "annualCap": 50, "fee": 2 } ] },
              "ganancias": { "brackets": [ { "upTo": 1000, "rate": 0.1 } ], "advances": 4 }
            }
            """;

        DataLoadResult result = GameDataLoader.LoadFromJson(Accounts, Commodities, Difficulties, "[]", taxes);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("dueDay"));
        Assert.Contains(result.Errors, e => e.Contains("iva.default"));
        Assert.Contains(result.Errors, e => e.Contains("crecientes (B)"));
        Assert.Contains(result.Errors, e => e.Contains("upTo: null"));
        Assert.Contains(result.Errors, e => e.Contains("advances"));
    }

    [Fact]
    public void InvalidMarketParameters_AreReported()
    {
        const string commodities = """
            [ { "id": "x", "name": "X", "sector": "S", "itemIds": ["1"], "allYear": true, "futuresContractSize": 1,
                "market": { "volatility": 0.9, "meanReversion": 0, "stockDecay": 1, "baseVolume": 0 } } ]
            """;

        DataLoadResult result = GameDataLoader.LoadFromJson(Accounts, commodities, Difficulties);
        Assert.Contains(result.Errors, e => e.Contains("volatility"));
        Assert.Contains(result.Errors, e => e.Contains("meanReversion"));
        Assert.Contains(result.Errors, e => e.Contains("stockDecay"));
        Assert.Contains(result.Errors, e => e.Contains("baseVolume"));
    }
}
