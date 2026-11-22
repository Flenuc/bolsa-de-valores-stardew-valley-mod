using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.Locations;
using StardewValley.Objects;
using xTile;
using xTile.ObjectModel;
using SObject = StardewValley.Object;

namespace ZuzuMarket.Game.Content;

/// <summary>
/// Interior de la Agencia Bursátil: ubicación propia (en <c>Data/Locations</c>), mapa copiado del galpón vanilla,
/// muebles vanilla y una Terminal Bursátil. La Terminal se saca antes de guardar (como el teléfono).
/// </summary>
internal sealed class Agency
{
    /// <summary>Muebles por palabra clave en el nombre interno (Data/Furniture), con su posición relativa.</summary>
    private static readonly (string[] Keywords, Func<AgencyLayout, Point> Tile)[] Decor =
    {
        (new[] { "Oak Table", "Walnut Table", "Table" }, l => l.Desk),
        (new[] { "Bookcase", "Book Shelf", "Bookshelf" }, l => new Point(1, l.Julian.Y - 1)),
        (new[] { "Bookcase", "Book Shelf", "Bookshelf" }, l => new Point(l.Width - 3, l.Julian.Y - 1)),
        (new[] { "House Plant", "Plant" }, l => new Point(1, l.Height - 3)),
        (new[] { "House Plant", "Plant" }, l => new Point(l.Width - 2, l.Height - 3)),
        (new[] { "Red Rug", "Brown Rug", "Rug" }, l => new Point((l.Width / 2) - 1, l.Height / 2)),
        (new[] { "Oak Chair", "Wooden Chair", "Chair" }, l => new Point(l.Desk.X, l.Desk.Y + 2)),
    };

    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly ModConfig _config;
    private readonly AgencyExterior _exterior;

    public Agency(IModHelper helper, IMonitor monitor, ModConfig config, AgencyExterior exterior)
    {
        _helper = helper;
        _monitor = monitor;
        _config = config;
        _exterior = exterior;
    }

    public GameLocation? Location => Game1.getLocationFromName(AgencyMap.LocationName);

    public void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (!_config.AgencyEnabled)
        {
            return;
        }

        if (e.NameWithoutLocale.IsEquivalentTo("Data/Locations"))
        {
            e.Edit(asset =>
            {
                var data = asset.AsDictionary<string, LocationData>().Data;
                data[AgencyMap.LocationName] = new LocationData
                {
                    DisplayName = _helper.Translation.Get("agency.name"),
                    CreateOnLoad = new CreateLocationData { MapPath = AgencyMap.Asset },
                    ExcludeFromNpcPathfinding = false,
                };
            });
        }
        else if (e.NameWithoutLocale.IsEquivalentTo(AgencyMap.Asset))
        {
            e.LoadFrom(BuildMap, AssetLoadPriority.Exclusive);
        }
    }

    private Map BuildMap()
    {
        // La salida depende de dónde quedó la fachada: se asegura que el pueblo ya se haya parchado.
        _helper.GameContent.Load<Map>(AgencyExterior.TownAsset);
        Map map = AgencyMap.Build();
        AgencyLayout layout = AgencyMap.Layout!;
        if (_exterior.Exit is { } exit)
        {
            map.Properties["Warp"] = new PropertyValue($"{layout.Door.X} {layout.Door.Y} Town {exit.X} {exit.Y}");
        }

        map.Properties.Remove("IsFarm");
        map.Properties.Remove("IsGreenhouse");
        return map;
    }

    /// <summary>Cada mañana: muebles (la primera vez) y la Terminal sobre el mostrador.</summary>
    public void Prepare()
    {
        if (Location is not { } location || AgencyMap.Layout is not { } layout)
        {
            return;
        }

        if (location.furniture.Count == 0)
        {
            Furnish(location, layout);
        }

        var tile = new Vector2(layout.Terminal.X, layout.Terminal.Y);
        if (!location.objects.ContainsKey(tile))
        {
            SObject terminal = ItemRegistry.Create<SObject>(ModObjects.TerminalQualifiedId);
            terminal.TileLocation = tile;
            location.objects.Add(tile, terminal);
        }
    }

    /// <summary>Antes de guardar: saca la Terminal (para que la partida no dependa del mod).</summary>
    public void Remove()
    {
        if (Location is not { } location)
        {
            return;
        }

        foreach (Vector2 tile in location.objects.Pairs.Where(p => p.Value.QualifiedItemId == ModObjects.TerminalQualifiedId).Select(p => p.Key).ToList())
        {
            location.objects.Remove(tile);
        }
    }

    /// <summary>Vuelve a construir el interior (cambió la posición de la fachada).</summary>
    public void Reload()
    {
        _helper.GameContent.InvalidateCache(AgencyMap.Asset);
        Location?.reloadMap();
    }

    private void Furnish(GameLocation location, AgencyLayout layout)
    {
        Dictionary<string, string> furniture = Game1.content.Load<Dictionary<string, string>>("Data/Furniture");
        foreach ((string[] keywords, Func<AgencyLayout, Point> where) in Decor)
        {
            string? id = keywords
                .Select(k => furniture.FirstOrDefault(f => f.Value.Split('/')[0].Equals(k, StringComparison.OrdinalIgnoreCase)).Key
                    ?? furniture.FirstOrDefault(f => f.Value.Split('/')[0].Contains(k, StringComparison.OrdinalIgnoreCase)).Key)
                .FirstOrDefault(k => k is not null);
            if (id is null)
            {
                continue;
            }

            Point p = where(layout);
            try
            {
                Furniture item = Furniture.GetFurnitureInstance(id, new Vector2(p.X, p.Y));
                if (item.canBePlacedHere(location, new Vector2(p.X, p.Y)))
                {
                    location.furniture.Add(item);
                }
            }
            catch (Exception ex)
            {
                _monitor.Log($"No se pudo poner el mueble {id} en la Agencia: {ex.Message}", LogLevel.Trace);
            }
        }
    }
}
