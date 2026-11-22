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
        Assert.Equal(3, result.Errors.Count);
    }
}
