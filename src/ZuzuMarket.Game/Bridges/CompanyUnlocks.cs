using StardewValley;
using ZuzuMarket.Core.Companies;

namespace ZuzuMarket.Game.Bridges;

/// <summary>Requisitos de desbloqueo de las sociedades, evaluados con el estado del juego.</summary>
internal static class CompanyUnlocks
{
    /// <summary>Barril, conservera, prensa de queso y barrica.</summary>
    private static readonly HashSet<string> Machines = new() { "(BC)12", "(BC)15", "(BC)16", "(BC)163" };

    public const int MachinesNeeded = 5;

    public static bool IsUnlocked(CompanyTypeDefinition type) => type.Unlock.ToLowerInvariant() switch
    {
        "none" => true,
        "mine40" => Game1.player.deepestMineLevel >= 40,
        "fishing6" => Game1.player.FishingLevel >= 6,
        "machines5" => CountMachines() >= MachinesNeeded,
        "year2" => Game1.year >= 2,
        _ => false,
    };

    public static int CountMachines()
    {
        int count = 0;
        Utility.ForEachLocation(location =>
        {
            count += location.objects.Values.Count(o => Machines.Contains(o.QualifiedItemId));
            return true;
        });
        return count;
    }
}
