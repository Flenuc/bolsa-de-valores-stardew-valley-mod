using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Simulation;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Tests;

/// <summary>Carga los JSON reales del mod (copiados al directorio de salida de los tests) y arma variantes.</summary>
internal static class TestData
{
    private static readonly Lazy<GameData> Lazy = new(() => Load());

    public static string DataDirectory => Path.Combine(AppContext.BaseDirectory, "data");

    public static GameData Real => Lazy.Value;

    public static string ReadFile(string name) => File.ReadAllText(Path.Combine(DataDirectory, name));

    /// <summary>Datos reales recién cargados (instancias propias, se pueden modificar sin afectar otros tests).</summary>
    public static GameData Load()
    {
        DataLoadResult result = GameDataLoader.LoadFromDirectory(DataDirectory);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        return result.Data!;
    }

    /// <summary>Datos reales sin eventos (para aislar el modelo de precios o la inflación).</summary>
    public static GameData WithoutEvents()
    {
        GameData real = Load();
        return new GameData(real.Chart, real.Commodities, real.Difficulties);
    }

    /// <summary>Datos reales sin eventos y sin ruido de precios.</summary>
    public static GameData Deterministic()
    {
        GameData data = WithoutEvents();
        foreach (CommodityDefinition c in data.Commodities)
        {
            c.Market.Volatility = 0;
        }

        return data;
    }

    public static GameData WithEvents(params EventDefinition[] events)
    {
        GameData real = Load();
        return new GameData(real.Chart, real.Commodities, real.Difficulties, events);
    }

    /// <summary>Avanza la simulación día por día hasta la fecha indicada (inclusive).</summary>
    public static void AdvanceTo(EconomySimulation sim, EconomicDate target, Weather weather = Weather.Sun)
    {
        while (sim.CurrentDate < target)
        {
            sim.OnDayStarted(sim.CurrentDate.AddDays(1), weather);
        }
    }
}
