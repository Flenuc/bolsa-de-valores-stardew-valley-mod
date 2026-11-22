using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Objects;

namespace ZuzuMarket.Game.Bridges;

/// <summary>
/// Embargo de bienes: secuestra objetos de los cofres de la granja y de la casa (de mayor a menor valor) y, en una
/// quiebra en dificultad Argentina con <c>AllowBuildingAuction</c>, remata edificios de la granja (del más barato al más caro).
/// </summary>
internal sealed class SeizureBridge
{
    /// <summary>Edificios que nunca se rematan.</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase) { "Farmhouse", "Shipping Bin", "Pet Bowl", "Cabin" };

    private readonly IMonitor _monitor;

    public SeizureBridge(IMonitor monitor)
    {
        _monitor = monitor;
    }

    /// <summary>Secuestra objetos hasta cubrir <paramref name="target"/> (valuados a <paramref name="rate"/> de su precio de venta).</summary>
    public decimal SeizeItems(decimal target, decimal rate)
    {
        var candidates = new List<(Chest Chest, Item Item, decimal Unit)>();
        foreach (GameLocation? location in new[] { Game1.getFarm(), Game1.getLocationFromName("FarmHouse") })
        {
            if (location is null)
            {
                continue;
            }

            foreach (Chest chest in location.objects.Values.OfType<Chest>())
            {
                foreach (Item? item in chest.Items)
                {
                    if (item is not null)
                    {
                        decimal unit = Utility.getSellToStorePriceOfItem(item, countStack: false) * rate;
                        if (unit > 0)
                        {
                            candidates.Add((chest, item, unit));
                        }
                    }
                }
            }
        }

        decimal value = 0;
        foreach ((Chest chest, Item item, decimal unit) in candidates.OrderByDescending(c => c.Unit))
        {
            if (value >= target)
            {
                break;
            }

            int take = (int)Math.Min(item.Stack, Math.Ceiling((target - value) / unit));
            value += unit * take;
            _monitor.Log($"Secuestro: {take} × {item.DisplayName} ({unit:0.##}g c/u).", LogLevel.Debug);
            if (take >= item.Stack)
            {
                chest.Items.Remove(item);
            }
            else
            {
                item.Stack -= take;
            }
        }

        return Math.Round(value, 2);
    }

    /// <summary>Remata edificios (del más barato al más caro) hasta cubrir <paramref name="target"/>. Devuelve lo obtenido.</summary>
    public decimal AuctionBuildings(decimal target, decimal rate, List<string> auctioned)
    {
        Farm farm = Game1.getFarm();
        var candidates = farm.buildings
            .Where(b => !b.isUnderConstruction() && !Protected.Contains(b.buildingType.Value))
            .Select(b => (Building: b, Value: (b.GetData()?.BuildCost ?? 0) * rate))
            .Where(c => c.Value > 0)
            .OrderBy(c => c.Value)
            .ToList();

        decimal value = 0;
        foreach ((Building building, decimal price) in candidates)
        {
            if (value >= target)
            {
                break;
            }

            string name = building.buildingType.Value;
            if (farm.destroyStructure(building))
            {
                value += price;
                auctioned.Add(name);
                _monitor.Log($"Remate del edificio {name} por {price:0}g.", LogLevel.Info);
            }
        }

        return value;
    }
}
