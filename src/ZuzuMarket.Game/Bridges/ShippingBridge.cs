using StardewValley;
using ZuzuMarket.Core.Simulation;
using SObject = StardewValley.Object;

namespace ZuzuMarket.Game.Bridges;

/// <summary>
/// Lee el cajón de envíos al terminar el día y registra las unidades de commodities vendidas como oferta.
/// (Las ventas directas a tiendas todavía no generan oferta: ver §17 del diseño.)
/// </summary>
internal static class ShippingBridge
{
    /// <summary>Devuelve la cantidad de unidades de commodities registradas.</summary>
    public static int RecordShipments(EconomySimulation simulation)
    {
        int units = 0;
        foreach (Item? item in Game1.getFarm().getShippingBin(Game1.player))
        {
            if (item is SObject obj && obj.Stack > 0 && simulation.RecordShipment(obj.ItemId, obj.Stack))
            {
                units += obj.Stack;
            }
        }

        return units;
    }
}
