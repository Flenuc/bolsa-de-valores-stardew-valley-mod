using StardewValley;
using ZuzuMarket.Core.Ledger;
using ZuzuMarket.Core.Simulation;

namespace ZuzuMarket.Game.Bridges;

/// <summary>
/// Puente entre el oro del jugador (<c>Game1.player.Money</c>) y la cuenta Caja.
/// En la Fase 0 todo cambio de oro que no originó el mod se asienta como "no clasificado";
/// las fases siguientes registran ventas, compras e impuestos con asientos específicos.
/// </summary>
internal static class MoneyBridge
{
    public static long PlayerGold => Game1.player.Money;

    public static JournalEntry? Reconcile(EconomySimulation simulation, string description) =>
        simulation.ReconcileCash(PlayerGold, description);
}
