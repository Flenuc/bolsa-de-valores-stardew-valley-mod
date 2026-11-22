using Microsoft.Xna.Framework;
using StardewValley;
using xTile;
using xTile.Layers;
using xTile.Tiles;

namespace ZuzuMarket.Game.Content;

/// <summary>
/// Mapa del interior de la Agencia: copia del interior vanilla del galpón (<c>Maps/Shed</c>) con warps propios.
/// No se modifica el original. Reemplazable por un <c>assets/agency_interior.tmx</c> propio.
/// </summary>
internal static class AgencyMap
{
    public const string LocationName = "ZuzuMarket_Agencia";
    public const string Asset = "Maps/" + LocationName;
    private const string Source = "Maps/Shed";

    /// <summary>Distribución calculada a partir del mapa (se calcula una vez).</summary>
    public static AgencyLayout? Layout { get; private set; }

    /// <summary>Copia el mapa del galpón tile por tile en un mapa nuevo.</summary>
    public static Map Build()
    {
        Map src = Game1.content.Load<Map>(Source);
        var map = new Map(LocationName);
        foreach (TileSheet sheet in src.TileSheets)
        {
            // Las propiedades del tilesheet dicen qué tiles son pared ("Passable F"): sin ellas se podría caminar por las paredes.
            var copy = new TileSheet(sheet.Id, map, sheet.ImageSource, sheet.SheetSize, sheet.TileSize);
            foreach (var property in sheet.Properties)
            {
                copy.Properties[property.Key] = property.Value;
            }

            map.AddTileSheet(copy);
        }

        foreach (Layer layer in src.Layers)
        {
            var copy = new Layer(layer.Id, map, layer.LayerSize, layer.TileSize);
            map.AddLayer(copy);
            for (int x = 0; x < layer.LayerWidth; x++)
            {
                for (int y = 0; y < layer.LayerHeight; y++)
                {
                    Tile? tile = layer.Tiles[x, y];
                    if (tile is null)
                    {
                        continue;
                    }

                    Tile frame = tile is AnimatedTile animated && animated.TileFrames.Length > 0 ? animated.TileFrames[0] : tile;
                    var clone = new StaticTile(copy, map.GetTileSheet(frame.TileSheet.Id), frame.BlendMode, frame.TileIndex);
                    foreach (var property in tile.Properties)
                    {
                        clone.Properties[property.Key] = property.Value;
                    }

                    copy.Tiles[x, y] = clone;
                }
            }
        }

        foreach (var property in src.Properties.Where(p => p.Key != "Warp"))
        {
            map.Properties[property.Key] = property.Value;
        }

        Layout ??= AgencyLayout.From(src);
        return map;
    }

    /// <summary>Calcula la distribución sin construir el mapa (para la fachada, los horarios y los eventos).</summary>
    public static AgencyLayout EnsureLayout() => Layout ??= AgencyLayout.From(Game1.content.Load<Map>(Source));
}

/// <summary>Posiciones del interior: puerta, llegada, escritorio de Julián, terminal y muebles.</summary>
internal sealed record AgencyLayout(int Width, int Height, Point Door, Point Arrival, Point Desk, Point Julian, Point Terminal)
{
    /// <summary>Piso: hay tile, no hay construcción y el tilesheet no lo marca como no pisable (las paredes).</summary>
    public static bool IsFloor(Layer back, Layer? buildings, int x, int y)
    {
        if (x < 0 || y < 0 || x >= back.LayerWidth || y >= back.LayerHeight || back.Tiles[x, y] is not { } tile || buildings?.Tiles[x, y] is not null)
        {
            return false;
        }

        // En walls_and_floors los índices < 336 son empapelado de pared (los pisos empiezan en 336).
        if (tile.TileSheet.ImageSource.Contains("walls_and_floors", StringComparison.OrdinalIgnoreCase) && tile.TileIndex < 336)
        {
            return false;
        }

        return !(tile.TileIndexProperties.TryGetValue("Passable", out var passable) && passable?.ToString() is "F" or "f")
            && !(tile.Properties.TryGetValue("Passable", out var own) && own?.ToString() is "F" or "f");
    }

    public static AgencyLayout From(Map map)
    {
        Layer back = map.GetLayer("Back");
        int width = back.LayerWidth;
        int height = back.LayerHeight;

        // La puerta: el warp original del galpón, o el centro de la última fila con piso.
        Point door = new(width / 2, height - 1);
        if (map.Properties.TryGetValue("Warp", out var warp) && warp?.ToString() is { } text)
        {
            string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && int.TryParse(parts[0], out int wx) && int.TryParse(parts[1], out int wy))
            {
                door = new Point(wx, wy);
            }
        }
        else
        {
            // Sin warp: la fila de piso más baja en el centro (pisable, sin construcciones).
            Layer? solid = map.GetLayer("Buildings");
            for (int y = height - 1; y >= 0; y--)
            {
                if (IsFloor(back, solid, width / 2, y))
                {
                    door = new Point(width / 2, y);
                    break;
                }
            }
        }

        // Primera fila de piso en el centro (debajo de la pared de arriba): con tile, sin construcciones y pisable.
        Layer? buildings = map.GetLayer("Buildings");
        int cx = width / 2;
        int floorTop = 3;
        for (int y = 0; y < height; y++)
        {
            if (IsFloor(back, buildings, cx, y) && IsFloor(back, buildings, cx, y + 1))
            {
                floorTop = y;
                break;
            }
        }

        Point arrival = new(door.X, Math.Max(1, door.Y - 1));
        Point julian = new(cx, floorTop);                       // contra la pared, detrás del escritorio
        Point desk = new(Math.Max(1, cx - 1), floorTop + 1);    // escritorio delante de él
        Point terminal = new(Math.Min(width - 4, cx + 3), floorTop);
        return new AgencyLayout(width, height, door, arrival, desk, julian, terminal);
    }
}
