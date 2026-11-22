using ZuzuMarket.Core.Data;

namespace ZuzuMarket.Core.Tests;

/// <summary>Carga los JSON reales del mod (copiados al directorio de salida de los tests).</summary>
internal static class TestData
{
    private static readonly Lazy<GameData> Lazy = new(() =>
    {
        DataLoadResult result = GameDataLoader.LoadFromDirectory(DataDirectory);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        return result.Data!;
    });

    public static string DataDirectory => Path.Combine(AppContext.BaseDirectory, "data");

    public static GameData Real => Lazy.Value;

    public static string ReadFile(string name) => File.ReadAllText(Path.Combine(DataDirectory, name));
}
