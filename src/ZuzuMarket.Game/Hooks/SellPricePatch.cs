using HarmonyLib;
using StardewModdingAPI;
using SObject = StardewValley.Object;

namespace ZuzuMarket.Game.Hooks;

/// <summary>
/// Reemplaza el precio de venta vanilla por el precio de mercado: multiplica el resultado de
/// <see cref="SObject.sellToStorePrice"/>, así se conservan la calidad y las profesiones.
/// Lo usan el cajón de envíos y la venta en tiendas.
/// </summary>
internal static class SellPricePatch
{
    private static IMonitor? _monitor;
    private static bool _errorLogged;

    public static void Apply(Harmony harmony, IMonitor monitor)
    {
        _monitor = monitor;
        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.sellToStorePrice)),
            postfix: new HarmonyMethod(typeof(SellPricePatch), nameof(Postfix)));
    }

    private static void Postfix(SObject __instance, ref int __result)
    {
        try
        {
            __result = PriceService.Apply(__result, PriceService.SellMultiplier(__instance.ItemId));
        }
        catch (Exception ex)
        {
            if (!_errorLogged)
            {
                _errorLogged = true;
                _monitor?.Log($"Error al calcular el precio de mercado de {__instance.ItemId}; se usa el precio vanilla.\n{ex}", LogLevel.Error);
            }
        }
    }
}
