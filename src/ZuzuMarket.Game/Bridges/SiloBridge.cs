using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;
using ZuzuMarket.Core.Markets;
using ZuzuMarket.Core.Simulation;
using SObject = StardewValley.Object;

namespace ZuzuMarket.Game.Bridges;

/// <summary>
/// Silo de Acopio virtual: se abre como un cofre del juego que solo acepta commodities y, al cerrarlo,
/// el contenido vuelve al estado del mod.
/// </summary>
internal sealed class SiloBridge
{
    private const int MaxStack = 999;

    private readonly Func<EconomySimulation?> _getSimulation;
    private Chest? _chest;

    public SiloBridge(Func<EconomySimulation?> getSimulation)
    {
        _getSimulation = getSimulation;
    }

    public void Open(EconomySimulation sim)
    {
        var chest = new Chest(playerChest: true);
        foreach (SiloItem silo in sim.Broker.Silo)
        {
            int remaining = silo.Stack;
            while (remaining > 0)
            {
                int stack = Math.Min(MaxStack, remaining);
                chest.Items.Add(ItemRegistry.Create("(O)" + silo.ItemId, stack, silo.Quality));
                remaining -= stack;
            }
        }

        _chest = chest;
        Game1.activeClickableMenu = new ItemGrabMenu(
            chest.Items,
            reverseGrab: false,
            showReceivingMenu: true,
            highlightFunction: item => item is SObject && sim.Broker.IsSiloItem(item.ItemId),
            behaviorOnItemSelectFunction: chest.grabItemFromInventory,
            message: null,
            behaviorOnItemGrab: chest.grabItemFromChest,
            snapToBottom: false,
            canBeExitedWithKey: true,
            playRightClickSound: true,
            allowRightClick: true,
            showOrganizeButton: true,
            source: ItemGrabMenu.source_chest,
            sourceItem: chest,
            whichSpecialButton: -1,
            context: chest);
    }

    /// <summary>Al cerrar el cofre: guarda lo que quedó y devuelve al jugador lo que no sea commodity.</summary>
    public void OnMenuChanged(object? sender, MenuChangedEventArgs e)
    {
        if (_chest is null || e.OldMenu is not ItemGrabMenu menu || menu.context != _chest || e.NewMenu is ItemGrabMenu { context: var ctx } && ctx == _chest)
        {
            return;
        }

        Chest chest = _chest;
        _chest = null;
        EconomySimulation? sim = _getSimulation();
        if (sim is null)
        {
            return;
        }

        var items = new List<SiloItem>();
        foreach (Item? item in chest.Items)
        {
            if (item is null)
            {
                continue;
            }

            if (item is SObject obj && sim.Broker.IsSiloItem(obj.ItemId))
            {
                items.Add(new SiloItem { ItemId = obj.ItemId, Quality = obj.Quality, Stack = obj.Stack, BasePrice = obj.Price });
            }
            else if (!Game1.player.addItemToInventoryBool(item))
            {
                Game1.createItemDebris(item, Game1.player.getStandingPosition(), Game1.player.FacingDirection);
            }
        }

        sim.Broker.SetSilo(items);
    }
}
