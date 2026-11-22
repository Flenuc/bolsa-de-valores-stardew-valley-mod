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

    /// <summary>Dónde quedó hoy (puede ser un tile cercano si el configurado está ocupado, p. ej. con SVE).</summary>
    public Vector2? PlacedAt { get; private set; }

    public void Place()
    {
        GameLocation? town = Game1.getLocationFromName(Location);
        if (town is null)
        {
            return;
        }

        if (town.objects.Values.FirstOrDefault(o => o.QualifiedItemId == ModObjects.PhoneQualifiedId) is { } placed)
        {
            PlacedAt = placed.TileLocation;
            return;
        }

        // El tile configurado, o el más cercano libre (otros mods, como SVE, pueden haber cambiado el pueblo).
        Vector2? tile = Candidates(Tile).Cast<Vector2?>().FirstOrDefault(t => town.CanItemBePlacedHere(t!.Value) && !town.objects.ContainsKey(t.Value));
        if (tile is not { } free)
        {
            PlacedAt = null;
            _monitor.Log($"No se pudo poner el teléfono de Valle Inversiones cerca de {Location} {Tile}: no hay lugar libre. Movelo con zuzu_place_phone.", LogLevel.Warn);
            return;
        }

        if (free != Tile)
        {
            _monitor.Log($"El tile del teléfono {Tile} está ocupado: se puso en {free}.", LogLevel.Debug);
        }

        SObject phone = ItemRegistry.Create<SObject>(ModObjects.PhoneQualifiedId);
        phone.TileLocation = free;
        town.objects.Add(free, phone);
        PlacedAt = free;
    }

    private static IEnumerable<Vector2> Candidates(Vector2 origin)
    {
        yield return origin;
        for (int r = 1; r <= 4; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == r)
                    {
                        yield return origin + new Vector2(dx, dy);
                    }
                }
            }
        }
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
