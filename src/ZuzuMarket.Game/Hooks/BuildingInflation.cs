using StardewModdingAPI.Events;
using StardewValley.GameData.Buildings;

namespace ZuzuMarket.Game.Hooks;

/// <summary>Aplica la inflación (IPC) al costo en oro de las construcciones de Robin.</summary>
internal static class BuildingInflation
{
    public const string Asset = "Data/Buildings";

    public static void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (!e.NameWithoutLocale.IsEquivalentTo(Asset) || PriceService.Simulation is null)
        {
            return;
        }

        double multiplier = PriceService.PurchaseMultiplier(null);
        if (multiplier == 1.0)
        {
            return;
        }

        e.Edit(asset =>
        {
            foreach (BuildingData building in asset.AsDictionary<string, BuildingData>().Data.Values)
            {
                building.BuildCost = PriceService.Apply(building.BuildCost, multiplier);
            }
        }, AssetEditPriority.Late);
    }
}
