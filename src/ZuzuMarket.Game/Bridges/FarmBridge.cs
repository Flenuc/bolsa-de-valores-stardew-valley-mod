using StardewValley;
using ZuzuMarket.Core.Taxation;

namespace ZuzuMarket.Game.Bridges;

/// <summary>Lo que el municipio ve de la granja para cobrar el ABL: nivel de la casa y edificios terminados.</summary>
internal static class FarmBridge
{
    public static FarmSnapshot Snapshot()
    {
        Farm farm = Game1.getFarm();
        bool greenhouseRepaired = Game1.MasterPlayer.mailReceived.Contains("ccPantry")
            || Game1.MasterPlayer.mailReceived.Contains("jojaPantry");

        var buildings = farm.buildings
            .Where(b => !b.isUnderConstruction())
            .Select(b => b.buildingType.Value)
            .Where(type => !string.IsNullOrEmpty(type) && (type != "Greenhouse" || greenhouseRepaired))
            .ToList();

        return new FarmSnapshot(Game1.player.HouseUpgradeLevel, buildings);
    }
}
