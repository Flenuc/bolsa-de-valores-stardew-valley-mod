using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using ZuzuMarket.Core.Simulation;
using ZuzuMarket.Core.Taxation;

namespace ZuzuMarket.Game.Bridges;

/// <summary>
/// Sigue el oro del jugador tick a tick para clasificar lo que pasa en las tiendas:
/// si baja con una tienda abierta es una compra (con factura); si sube, una venta a la tienda.
/// El resto de los cambios (regalos, recompensas, la casa de Robin…) los toma la conciliación al guardar.
/// </summary>
internal sealed class MoneyTracker
{
    private readonly Func<EconomySimulation?> _getSimulation;
    private readonly List<SoldItem> _soldToShop = new();
    private long _lastGold;
    private long _pendingShopIncome;
    private bool _wasInCommerceMenu;

    public MoneyTracker(Func<EconomySimulation?> getSimulation)
    {
        _getSimulation = getSimulation;
    }

    /// <summary>Toma el oro actual como referencia (al cargar, al empezar el día o después de que el mod cobre o pague).</summary>
    public void Reset()
    {
        _lastGold = Context.IsWorldReady ? Game1.player.Money : 0;
        _pendingShopIncome = 0;
        _soldToShop.Clear();
    }

    public static bool IsCommerceMenu(IClickableMenu? menu) =>
        menu is ShopMenu or CarpenterMenu or PurchaseAnimalsMenu or GeodeMenu;

    /// <summary>Objetos que salen del inventario con una tienda abierta: se vendieron.</summary>
    public void OnInventoryChanged(object? sender, InventoryChangedEventArgs e)
    {
        if (!e.IsLocalPlayer || Game1.activeClickableMenu is not ShopMenu)
        {
            return;
        }

        foreach (Item item in e.Removed)
        {
            AddSold(item, item.Stack);
        }

        foreach (ItemStackSizeChange change in e.QuantityChanged)
        {
            if (change.NewSize < change.OldSize)
            {
                AddSold(change.Item, change.OldSize - change.NewSize);
            }
        }
    }

    public void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        EconomySimulation? sim = _getSimulation();
        if (sim is null || !Context.IsWorldReady)
        {
            return;
        }

        bool inCommerce = IsCommerceMenu(Game1.activeClickableMenu);
        long gold = Game1.player.Money;
        long delta = gold - _lastGold;
        _lastGold = gold;

        if (inCommerce && delta < 0)
        {
            sim.RecordPurchase(-delta);
        }
        else if (inCommerce && delta > 0)
        {
            _pendingShopIncome += delta;
        }

        // Las ventas se asientan cuando ya se sabe qué objetos salieron (o al cerrar la tienda).
        if (_pendingShopIncome > 0 && (_soldToShop.Count > 0 || (_wasInCommerceMenu && !inCommerce)))
        {
            sim.RecordShopSales(Allocate(_pendingShopIncome, _soldToShop));
            _pendingShopIncome = 0;
            _soldToShop.Clear();
        }

        _wasInCommerceMenu = inCommerce;
    }

    private void AddSold(Item item, int units)
    {
        if (units > 0)
        {
            _soldToShop.Add(new SoldItem(item.ItemId, item.Category, units, Utility.getSellToStorePriceOfItem(item, countStack: false) * (long)units));
        }
    }

    /// <summary>Reparte el oro cobrado entre los objetos vendidos, en proporción a su valor estimado.</summary>
    private static List<SoldItem> Allocate(long income, List<SoldItem> sold)
    {
        if (sold.Count == 0)
        {
            return new List<SoldItem> { new("", 0, 0, income) };
        }

        long weight = sold.Sum(s => s.Gross);
        var result = new List<SoldItem>(sold.Count);
        long assigned = 0;
        for (int i = 0; i < sold.Count; i++)
        {
            long gross = i == sold.Count - 1
                ? income - assigned
                : weight > 0 ? income * sold[i].Gross / weight : 0;
            assigned += gross;
            result.Add(sold[i] with { Gross = gross });
        }

        return result;
    }
}
