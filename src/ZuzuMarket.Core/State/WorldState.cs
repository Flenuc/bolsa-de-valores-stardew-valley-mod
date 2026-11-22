using System.Text.Json;
using System.Text.Json.Serialization;
using ZuzuMarket.Core.Commodities;
using ZuzuMarket.Core.Events;
using ZuzuMarket.Core.Ledger;
using ZuzuMarket.Core.Macro;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.State;

/// <summary>Todo el estado del mod para una partida. Se guarda junto con la partida.</summary>
public sealed class WorldState
{
    /// <summary>1: Fase 0. 2: inflación, commodities y eventos.</summary>
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Semilla de la partida (en el juego: <c>Game1.uniqueIDForThisGame</c>).</summary>
    public ulong Seed { get; set; }

    /// <summary>Fecha en que el mod empezó a simular esta partida.</summary>
    public EconomicDate CreatedOn { get; set; } = EconomicDate.Start;

    public EconomicDate CurrentDate { get; set; } = EconomicDate.Start;

    public string DifficultyId { get; set; } = "realista";

    public LedgerState Ledger { get; set; } = new();

    public InflationState Inflation { get; set; } = new();

    public Dictionary<string, CommodityState> Commodities { get; set; } = new();

    public EventsState Events { get; set; } = new();
}

public sealed class WorldStateSerializationException : Exception
{
    public WorldStateSerializationException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

public static class WorldStateSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(WorldState state) => JsonSerializer.Serialize(state, Options);

    public static WorldState Deserialize(string json)
    {
        WorldState? state;
        try
        {
            state = JsonSerializer.Deserialize<WorldState>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new WorldStateSerializationException("El estado guardado está dañado.", ex);
        }

        if (state is null)
        {
            throw new WorldStateSerializationException("El estado guardado está vacío.");
        }

        if (state.SchemaVersion > WorldState.CurrentSchemaVersion)
        {
            throw new WorldStateSerializationException(
                $"La partida se guardó con una versión más nueva del mod (esquema {state.SchemaVersion}; esta versión soporta hasta {WorldState.CurrentSchemaVersion}).");
        }

        return Migrate(state);
    }

    /// <summary>Actualiza estados guardados con esquemas viejos. Cada versión nueva agrega su paso acá.</summary>
    private static WorldState Migrate(WorldState state)
    {
        // 1 → 2: los módulos nuevos arrancan vacíos (los inicializadores ya los crean al deserializar)
        // y la simulación los pone en marcha al cargar: IPC = 1, precios en su nivel normal, sin eventos.
        state.Inflation ??= new InflationState();
        state.Commodities ??= new Dictionary<string, CommodityState>();
        state.Events ??= new EventsState();
        state.SchemaVersion = WorldState.CurrentSchemaVersion;
        return state;
    }
}
