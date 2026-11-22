using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using ZuzuMarket.Core.Markets;

namespace ZuzuMarket.Game.Content;

/// <summary>Cartas y avisos de Valle Inversiones: bienvenida con la receta de la Terminal, dividendos y vencimientos.</summary>
internal sealed class BrokerMail
{
    private const string MailAsset = "Data/mail";

    private readonly IModHelper _helper;
    private readonly ModConfig _config;
    private readonly TaxMail _hud;
    private readonly string _welcomeKey;

    public BrokerMail(IModHelper helper, IManifest manifest, ModConfig config, TaxMail hud)
    {
        _helper = helper;
        _config = config;
        _hud = hud;
        _welcomeKey = manifest.UniqueID + ".Broker.Bienvenida";
    }

    private ITranslationHelper I18n => _helper.Translation;

    public void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (!e.NameWithoutLocale.IsEquivalentTo(MailAsset))
        {
            return;
        }

        e.Edit(asset =>
        {
            string body = I18n.Get("broker.mail.welcome").ToString().Replace("\n", "^");
            asset.AsDictionary<string, string>().Data[_welcomeKey] =
                $"{body}^^   -{I18n.Get("broker.signature")}%item craftingRecipe {ModObjects.TerminalId} %%[#]{I18n.Get("broker.mail.welcome.title")}";
        });
    }

    /// <summary>Al abrir la cuenta: carta de bienvenida con la receta de la Terminal (siempre, aunque las cartas estén desactivadas).</summary>
    public void SendWelcome()
    {
        if (!Game1.player.mailbox.Contains(_welcomeKey) && !Game1.player.mailReceived.Contains(_welcomeKey))
        {
            Game1.player.mailbox.Add(_welcomeKey);
        }

        _hud.Hud(I18n.Get("broker.hud.opened"));
    }

    public void Publish(IReadOnlyList<MarketNotice> notices, bool accountOpen)
    {
        if (!accountOpen)
        {
            return;
        }

        foreach (MarketNotice n in notices)
        {
            string? text = n.Kind switch
            {
                MarketNoticeKind.Dividend => I18n.Get("broker.hud.dividend", new { ticker = n.Instrument, amount = Formatting.Gold(I18n, (double)n.Amount) }),
                MarketNoticeKind.LetraMatured => I18n.Get("broker.hud.letra", new { id = n.Instrument, amount = Formatting.Gold(I18n, (double)n.Amount) }),
                MarketNoticeKind.CaucionMatured => I18n.Get("broker.hud.caucion", new { amount = Formatting.Gold(I18n, (double)n.Amount) }),
                _ => null,
            };
            if (text is not null)
            {
                _hud.Hud(text);
            }
        }
    }
}
