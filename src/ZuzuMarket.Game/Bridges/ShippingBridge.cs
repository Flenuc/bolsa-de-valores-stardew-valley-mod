using StardewValley;
using ZuzuMarket.Core.Taxation;

namespace ZuzuMarket.Game.Bridges;

/// <summary>
/// Lee el cajón de envíos al terminar el día: cada objeto con su valor de venta (precio de mercado, calidad y
/// profesiones incluidas), para sumarlo como oferta y contabilizarlo como venta a cobrar.
/// </summary>
internal static class ShippingBridge
{
    public static List<SoldItem> CollectShipments()
    {
        var items = new List<SoldItem>();
        foreach (Item? item in Game1.getFarm().getShippingBin(Game1.player))
        {
            // Los billetes gotoro no son una venta (no pagan impuestos): el oro llega y se concilia.
            if (item is null || item.Stack <= 0 || item.QualifiedItemId == Content.ModObjects.BillQualifiedId)
            {
                continue;
            }

            int gross = Utility.getSellToStorePriceOfItem(item);
            items.Add(new SoldItem(item.ItemId, item.Category, item.Stack, gross));
        }

        return items;
    }
}
