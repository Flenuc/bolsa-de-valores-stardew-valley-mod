using StardewModdingAPI;

namespace ZuzuMarket.Game.Compat;

/// <summary>
/// Detección de mods que cambian el pueblo (Stardew Valley Expanded) y posiciones por defecto de cada variante.
/// El mod nunca reemplaza mapas: solo parches chicos que se validan contra el mapa ya editado por los demás.
/// </summary>
internal sealed class ModCompat
{
    private static readonly string[] SveIds = { "FlashShifter.SVECode", "FlashShifter.StardewValleyExpandedCP" };

    public ModCompat(IModRegistry registry)
    {
        IsSve = SveIds.Any(registry.IsLoaded);
    }

    public bool IsSve { get; }

    public string Variant => IsSve ? "Stardew Valley Expanded" : "vanilla";

    /// <summary>Punto desde donde se busca lugar para la Agencia (el lote al sur de la plaza).</summary>
    public TilePosition AgencyOrigin => IsSve ? new TilePosition { X = 52, Y = 70 } : new TilePosition { X = 48, Y = 64 };

    /// <summary>Teléfono de Valle Inversiones por defecto.</summary>
    public TilePosition PhoneDefault => IsSve ? new TilePosition { X = 50, Y = 58 } : new TilePosition { X = 45, Y = 55 };
}
