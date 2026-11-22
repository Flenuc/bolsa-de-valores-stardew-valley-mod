using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using xTile;
using xTile.Layers;
using xTile.ObjectModel;
using xTile.Tiles;
using ZuzuMarket.Game.Compat;

namespace ZuzuMarket.Game.Content;

/// <summary>
/// Fachada de la Agencia Bursátil en el pueblo (3 tiles de ancho: techo + 3 filas). Se parcha al final, sobre el mapa
/// que ya editaron los demás mods (p. ej. Stardew Valley Expanded), y solo si encuentra un lugar libre: piso, sin
/// construcciones, sin warps ni acciones. Si no hay lugar, no toca nada (queda el teléfono).
/// </summary>
internal sealed class AgencyExterior
{
    public const string TownAsset = "Maps/Town";
    private const string SheetId = "zzm_agency_exterior";
    private const int Width = 3;
    private const int Height = 4;
    private const int SearchRadius = 25;

    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly ModConfig _config;
    private readonly ModCompat _compat;

    public AgencyExterior(IModHelper helper, IMonitor monitor, ModConfig config, ModCompat compat)
    {
        _helper = helper;
        _monitor = monitor;
        _config = config;
        _compat = compat;
    }

    /// <summary>Esquina superior izquierda de la fachada (null = no se pudo poner).</summary>
    public Point? Placement { get; private set; }

    /// <summary>Tile de la puerta (se entra caminando sobre ella).</summary>
    public Point? Door => Placement is { } p ? new Point(p.X + 1, p.Y + Height - 1) : null;

    /// <summary>Dónde aparece el jugador al salir (delante de la puerta).</summary>
    public Point? Exit => Door is { } d ? new Point(d.X, d.Y + 1) : null;

    public string? Problem { get; private set; }

    public void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (!_config.AgencyEnabled || !e.NameWithoutLocale.IsEquivalentTo(TownAsset))
        {
            return;
        }

        e.Edit(asset => Patch(asset.AsMap().Data), AssetEditPriority.Late + 1000);
    }

    /// <summary>Vuelve a parchar el pueblo (cambió la posición).</summary>
    public void Reload()
    {
        _helper.GameContent.InvalidateCache(TownAsset);
        Game1.getLocationFromName("Town")?.reloadMap();
    }

    private void Patch(Map map)
    {
        Placement = null;
        Problem = null;
        Layer? back = map.GetLayer("Back");
        Layer? buildings = map.GetLayer("Buildings");
        Layer? front = map.GetLayer("Front");
        if (back is null || buildings is null || front is null)
        {
            Problem = "el mapa del pueblo no tiene las capas esperadas";
            return;
        }

        HashSet<Point> warps = WarpTiles(map);
        TilePosition origin = _config.AgencyPosition ?? _compat.AgencyOrigin;
        Point? spot = _config.AgencyPosition is not null && IsFree(map, back, buildings, front, warps, origin.X, origin.Y)
            ? new Point(origin.X, origin.Y)
            : _config.AgencyPosition is not null ? null : Search(map, back, buildings, front, warps, origin.X, origin.Y);
        if (spot is not { } p)
        {
            Problem = _config.AgencyPosition is not null
                ? $"la posición configurada ({origin.X}, {origin.Y}) no está libre"
                : $"no hay un lugar libre de {Width}×{Height + 1} cerca de ({origin.X}, {origin.Y})";
            _monitor.Log($"Agencia Bursátil: {Problem}. Queda el teléfono de Valle Inversiones; podés elegir el lugar con zuzu_agency here.", LogLevel.Warn);
            return;
        }

        var sheet = new TileSheet(SheetId, map, _helper.ModContent.GetInternalAssetName("assets/agency_exterior.png").BaseName,
            new xTile.Dimensions.Size(Width, Height), new xTile.Dimensions.Size(16, 16));
        if (map.GetTileSheet(SheetId) is null)
        {
            map.AddTileSheet(sheet);
            map.LoadTileSheets(Game1.mapDisplayDevice);
        }

        sheet = map.GetTileSheet(SheetId);
        for (int dx = 0; dx < Width; dx++)
        {
            front.Tiles[p.X + dx, p.Y] = new StaticTile(front, sheet, BlendMode.Alpha, dx);
            for (int dy = 1; dy < Height; dy++)
            {
                buildings.Tiles[p.X + dx, p.Y + dy] = new StaticTile(buildings, sheet, BlendMode.Alpha, (dy * Width) + dx);
            }
        }

        // La puerta se puede pisar: es un warp común (así también la usan los NPC para entrar y salir).
        Point door = new(p.X + 1, p.Y + Height - 1);
        buildings.Tiles[door.X, door.Y].Properties["Passable"] = new PropertyValue("T");
        AgencyLayout layout = AgencyMap.EnsureLayout();
        AddWarp(map, $"{door.X} {door.Y} {AgencyMap.LocationName} {layout.Arrival.X} {layout.Arrival.Y}");
        Placement = p;
        _monitor.Log($"Agencia Bursátil en el pueblo ({_compat.Variant}): fachada en ({p.X}, {p.Y}), puerta en ({door.X}, {door.Y}).", LogLevel.Debug);
    }

    private static void AddWarp(Map map, string warp)
    {
        string existing = map.Properties.TryGetValue("Warp", out PropertyValue? value) ? value.ToString() : "";
        map.Properties["Warp"] = new PropertyValue(string.IsNullOrWhiteSpace(existing) ? warp : existing.Trim() + " " + warp);
    }

    private static HashSet<Point> WarpTiles(Map map)
    {
        var tiles = new HashSet<Point>();
        if (map.Properties.TryGetValue("Warp", out PropertyValue? value))
        {
            string[] parts = value.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 4 < parts.Length; i += 5)
            {
                if (int.TryParse(parts[i], out int x) && int.TryParse(parts[i + 1], out int y))
                {
                    tiles.Add(new Point(x, y));
                }
            }
        }

        return tiles;
    }

    /// <summary>Busca en espiral (cuadrados crecientes) desde el origen.</summary>
    private static Point? Search(Map map, Layer back, Layer buildings, Layer front, HashSet<Point> warps, int ox, int oy)
    {
        for (int r = 0; r <= SearchRadius; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == r && IsFree(map, back, buildings, front, warps, ox + dx, oy + dy))
                    {
                        return new Point(ox + dx, oy + dy);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// La fachada (y un margen de un tile alrededor, más la fila delante de la puerta) tiene que caer sobre piso común:
    /// sin construcciones, sin agua, sin warps ni acciones, y sin nada en la capa de adelante donde va la fachada.
    /// </summary>
    private static bool IsFree(Map map, Layer back, Layer buildings, Layer front, HashSet<Point> warps, int x0, int y0)
    {
        for (int x = x0 - 1; x <= x0 + Width; x++)
        {
            for (int y = y0; y <= y0 + Height; y++)
            {
                if (x < 0 || y < 0 || x >= back.LayerWidth || y >= back.LayerHeight)
                {
                    return false;
                }

                Tile? floor = back.Tiles[x, y];
                if (floor is null || buildings.Tiles[x, y] is not null || warps.Contains(new Point(x, y)))
                {
                    return false;
                }

                if (floor.Properties.ContainsKey("Water") || floor.Properties.ContainsKey("TouchAction") || floor.Properties.ContainsKey("Action")
                    || floor.TileIndexProperties.ContainsKey("Water"))
                {
                    return false;
                }

                bool facade = x >= x0 && x < x0 + Width && y < y0 + Height;
                if (facade && front.Tiles[x, y] is not null)
                {
                    return false;
                }
            }
        }

        return map.GetLayer("AlwaysFront") is not { } always
            || Enumerable.Range(x0, Width).All(x => Enumerable.Range(y0, Height).All(y => always.Tiles[x, y] is null));
    }
}
