using StardewModdingAPI;
using ZuzuMarket.Core.State;

namespace ZuzuMarket.Game.Persistence;

/// <summary>Sobre que se guarda dentro de la partida. El estado va como JSON propio de Core.</summary>
internal sealed class SaveEnvelope
{
    public int Version { get; set; }

    public string Json { get; set; } = "";
}

/// <summary>Lee y escribe el estado del mod en el archivo de guardado de la partida.</summary>
internal sealed class SaveDataStore
{
    private const string Key = "world-state";
    private readonly IDataHelper _data;

    public SaveDataStore(IDataHelper data)
    {
        _data = data;
    }

    /// <summary>Devuelve el estado guardado, o <c>null</c> si la partida todavía no tiene datos del mod.</summary>
    /// <exception cref="WorldStateSerializationException">Si los datos están dañados o son de una versión más nueva.</exception>
    public WorldState? Load()
    {
        SaveEnvelope? envelope = _data.ReadSaveData<SaveEnvelope>(Key);
        if (envelope is null || string.IsNullOrWhiteSpace(envelope.Json))
        {
            return null;
        }

        return WorldStateSerializer.Deserialize(envelope.Json);
    }

    public void Save(WorldState state) => _data.WriteSaveData(Key, new SaveEnvelope
    {
        Version = state.SchemaVersion,
        Json = WorldStateSerializer.Serialize(state),
    });
}
