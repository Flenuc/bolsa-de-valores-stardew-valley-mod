using System.Runtime.CompilerServices;
using StardewModdingAPI.Events;
using StardewValley.Menus;

namespace ZuzuMarket.Game.Hooks;

/// <summary>
/// Aplica la inflación (IPC) a los precios de compra de las tiendas (<see cref="ShopMenu"/>).
/// Pendiente: construcciones de Robin y mejoras de herramientas de Clint, que no usan ShopMenu.
/// </summary>
internal static class ShopInflation
{
    /// <summary>Menús ya ajustados, para no aplicar la inflación dos veces al mismo menú.</summary>
    private static readonly ConditionalWeakTable<ShopMenu, object> Adjusted = new();

    public static void OnMenuChanged(object? sender, MenuChangedEventArgs e)
    {
        if (e.NewMenu is not ShopMenu shop || PriceService.Simulation is null || Adjusted.TryGetValue(shop, out _))
        {
            return;
        }

        Adjusted.Add(shop, new object());
        double multiplier = PriceService.PurchaseMultiplier;
        if (multiplier == 1.0)
        {
            return;
        }

        foreach (var item in shop.itemPriceAndStock.Keys.ToList())
        {
            var stock = shop.itemPriceAndStock[item];
            if (stock.Price > 0)
            {
                stock.Price = PriceService.Apply(stock.Price, multiplier);
                shop.itemPriceAndStock[item] = stock;
            }
        }
    }
}
