using ZuzuMarket.Core.Simulation;

namespace ZuzuMarket.Game.Hooks;

/// <summary>
/// Acceso estático a la simulación para los parches de Harmony (que no pueden recibir dependencias).
/// Es <c>null</c> en el menú principal, para granjeros que no son el jugador principal, o si el mod falló al cargar:
/// en esos casos el juego usa sus precios vanilla.
/// </summary>
internal static class PriceService
{
    public static EconomySimulation? Simulation { get; set; }

    /// <summary>Multiplicador del precio de venta vanilla de un objeto (1 si la simulación no está activa).</summary>
    /// <remarks>Incluye el precio FAS (retenciones) y la bonificación de tus sociedades.</remarks>
    public static double SellMultiplier(string itemId, int category) => Simulation?.SellMultiplier(itemId, category) ?? 1.0;

    /// <summary>Multiplicador de los precios de compra de una tienda (IPC y, si importa, aranceles).</summary>
    public static double PurchaseMultiplier(string? shopId) => Simulation?.PurchaseMultiplier(shopId) ?? 1.0;

    public static int Apply(int price, double multiplier) =>
        price <= 0 || multiplier == 1.0 ? price : Math.Max(1, (int)Math.Round(price * multiplier, MidpointRounding.AwayFromZero));
}
