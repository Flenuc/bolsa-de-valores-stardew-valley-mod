using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using SObject = StardewValley.Object;

namespace ZuzuMarket.Game.Content;

/// <summary>
/// Coloca el teléfono de Valle Inversiones en el pueblo cada mañana y lo saca antes de guardar,
/// para que la partida no dependa del mod si se desinstala.
/// </summary>
internal sealed class PhoneService
{
    private const string Location = "Town";

    private readonly IMonitor _monitor;
    private readonly ModConfig _config;

    public PhoneService(IMonitor monitor, ModConfig config)
    {
        _monitor = monitor;
        _config = config;
    }

    public Vector2 Tile => new(_config.PhonePosition.X, _config.PhonePosition.Y);

    public void Place()
    {
        GameLocation? town = Game1.getLocationFromName(Location);
        if (town is null)
        {
            return;
        }

        if (town.objects.TryGetValue(Tile, out SObject? existing))
        {
            if (existing.QualifiedItemId != ModObjects.PhoneQualifiedId)
            {
                _monitor.Log($"No se pudo poner el teléfono de Valle Inversiones en {Location} {Tile}: el lugar está ocupado. Movelo con zuzu_place_phone.", LogLevel.Warn);
            }

            return;
        }

        SObject phone = ItemRegistry.Create<SObject>(ModObjects.PhoneQualifiedId);
        phone.TileLocation = Tile;
        town.objects.Add(Tile, phone);
    }

    public void Remove()
    {
        GameLocation? town = Game1.getLocationFromName(Location);
        if (town is null)
        {
            return;
        }

        foreach (Vector2 tile in town.objects.Pairs.Where(p => p.Value.QualifiedItemId == ModObjects.PhoneQualifiedId).Select(p => p.Key).ToList())
        {
            town.objects.Remove(tile);
        }
    }

    /// <summary>Mueve el teléfono a otro tile del pueblo.</summary>
    public void MoveTo(int x, int y)
    {
        Remove();
        _config.PhonePosition = new TilePosition { X = x, Y = y };
        Place();
    }
}
