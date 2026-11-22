using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley.GameData.BigCraftables;

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

    /// <summary>Receta: 25 madera, 10 mineral de cobre, 1 cuarzo.</summary>
    private const string TerminalIngredients = "388 25 378 10 80 1";

    private readonly ITranslationHelper _i18n;

    public ModObjects(ITranslationHelper i18n)
    {
        _i18n = i18n;
    }

    public static bool IsBrokerObject(string? qualifiedItemId) =>
        qualifiedItemId is PhoneQualifiedId or TerminalQualifiedId;

    public void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (e.NameWithoutLocale.IsEquivalentTo(TextureAsset))
        {
            e.LoadFromModFile<Microsoft.Xna.Framework.Graphics.Texture2D>("assets/objects.png", AssetLoadPriority.Exclusive);
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
