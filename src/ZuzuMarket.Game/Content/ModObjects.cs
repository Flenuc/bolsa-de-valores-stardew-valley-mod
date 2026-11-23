using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley.GameData.BigCraftables;
using StardewValley.GameData.Objects;
using StardewValley.GameData.Shops;
using ZuzuMarket.Core.Simulation;

namespace ZuzuMarket.Game.Content;

/// <summary>
/// Objetos del mod: el teléfono de Valle Inversiones (en el pueblo) y la Terminal Bursátil (fabricable).
/// Se agregan a <c>Data/BigCraftables</c> con texturas provisorias de <c>assets/objects.png</c>.
/// </summary>
internal sealed class ModObjects
{
    public const string PhoneId = "Flenuc.ZuzuMarket_Phone";
    public const string TerminalId = "Flenuc.ZuzuMarket_Terminal";
    public const string PhoneQualifiedId = "(BC)" + PhoneId;
    public const string TerminalQualifiedId = "(BC)" + TerminalId;
    public const string TextureAsset = "Mods/Flenuc.ZuzuMarket/Objects";
    public const string BillId = "Flenuc.ZuzuMarket_GotoroBill";
    public const string BillQualifiedId = "(O)" + BillId;
    public const string BillTextureAsset = "Mods/Flenuc.ZuzuMarket/GotoroBill";

    /// <summary>Tienda de Krobus en las cloacas.</summary>
    public const string KrobusShop = "ShadowShop";

    /// <summary>Receta: 25 madera, 10 mineral de cobre, 1 cuarzo.</summary>
    private const string TerminalIngredients = "388 25 378 10 80 1";

    private readonly ITranslationHelper _i18n;
    private readonly Func<EconomySimulation?> _getSimulation;

    public ModObjects(ITranslationHelper i18n, Func<EconomySimulation?> getSimulation)
    {
        _i18n = i18n;
        _getSimulation = getSimulation;
    }

    /// <summary>Blue: Krobus vende el billete a esta punta (oro local por billete).</summary>
    public static int BlueAsk(EconomySimulation sim) =>
        (int)Math.Ceiling(sim.Fx.Blue(sim.FxRules) * (1 + ((double)sim.FxRules.BlueSpread / 2)));

    /// <summary>Blue: Krobus (o el cajón) compra el billete a esta punta. Sin cepo, al oficial comprador.</summary>
    public static int BlueBid(EconomySimulation sim) => sim.FxRules.Cepo
        ? (int)Math.Floor(sim.Fx.Blue(sim.FxRules) * (1 - ((double)sim.FxRules.BlueSpread / 2)))
        : (int)Math.Floor(sim.Broker.OfficialBid);

    public static bool IsBrokerObject(string? qualifiedItemId) =>
        qualifiedItemId is PhoneQualifiedId or TerminalQualifiedId;

    public void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (e.NameWithoutLocale.IsEquivalentTo(TextureAsset))
        {
            e.LoadFromModFile<Microsoft.Xna.Framework.Graphics.Texture2D>("assets/objects.png", AssetLoadPriority.Exclusive);
        }
        else if (e.NameWithoutLocale.IsEquivalentTo(BillTextureAsset))
        {
            e.LoadFromModFile<Microsoft.Xna.Framework.Graphics.Texture2D>("assets/gotoro_bill.png", AssetLoadPriority.Exclusive);
        }
        else if (e.NameWithoutLocale.IsEquivalentTo("Data/Objects"))
        {
            e.Edit(asset => asset.AsDictionary<string, ObjectData>().Data[BillId] = new ObjectData
            {
                Name = BillId,
                DisplayName = _i18n.Get("object.bill.name"),
                Description = _i18n.Get("object.bill.description"),
                Type = "Basic",
                Category = 0,
                Price = 50,
                Texture = BillTextureAsset,
                SpriteIndex = 0,
                Edibility = -300,
                ExcludeFromRandomSale = true,
                ExcludeFromFishingCollection = true,
                ExcludeFromShippingCollection = true,
            });
        }
        else if (e.NameWithoutLocale.IsEquivalentTo("Data/Shops"))
        {
            // Solo con cepo: Krobus vende billetes gotoro al blue y los compra de vuelta.
            if (_getSimulation() is not { } sim || !sim.FxRules.Cepo)
            {
                return;
            }

            e.Edit(asset =>
            {
                IDictionary<string, ShopData> shops = asset.AsDictionary<string, ShopData>().Data;
                if (!shops.TryGetValue(KrobusShop, out ShopData? shop))
                {
                    return;
                }

                shop.Items ??= new List<ShopItemData>();
                shop.Items.RemoveAll(i => i.Id == BillId);
                shop.Items.Insert(0, new ShopItemData { Id = BillId, ItemId = BillQualifiedId, Price = BlueAsk(sim), AvailableStock = -1 });
                shop.SalableItemTags ??= new List<string>();
                string tag = "id_" + BillQualifiedId.ToLowerInvariant();
                if (!shop.SalableItemTags.Contains(tag))
                {
                    shop.SalableItemTags.Add(tag);
                }
            }, AssetEditPriority.Late);
        }
        else if (e.NameWithoutLocale.IsEquivalentTo("Data/BigCraftables"))
        {
            e.Edit(asset =>
            {
                IDictionary<string, BigCraftableData> data = asset.AsDictionary<string, BigCraftableData>().Data;
                data[PhoneId] = new BigCraftableData
                {
                    Name = PhoneId,
                    DisplayName = _i18n.Get("object.phone.name"),
                    Description = _i18n.Get("object.phone.description"),
                    Price = 0,
                    Fragility = 2, // indestructible: no se puede levantar ni romper
                    CanBePlacedOutdoors = true,
                    CanBePlacedIndoors = true,
                    Texture = TextureAsset,
                    SpriteIndex = 0,
                };
                data[TerminalId] = new BigCraftableData
                {
                    Name = TerminalId,
                    DisplayName = _i18n.Get("object.terminal.name"),
                    Description = _i18n.Get("object.terminal.description"),
                    Price = 500,
                    Fragility = 0,
                    CanBePlacedOutdoors = true,
                    CanBePlacedIndoors = true,
                    Texture = TextureAsset,
                    SpriteIndex = 1,
                };
            });
        }
        else if (e.NameWithoutLocale.IsEquivalentTo("Data/CraftingRecipes"))
        {
            e.Edit(asset =>
            {
                // ingredientes / sin uso / resultado / es big craftable / condición de desbloqueo / nombre visible
                asset.AsDictionary<string, string>().Data[TerminalId] =
                    $"{TerminalIngredients}/Home/{TerminalId}/true/none/{_i18n.Get("object.terminal.name")}";
            });
        }
    }
}
