using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using ZuzuMarket.Core.Legal;

namespace ZuzuMarket.Game.Content;

/// <summary>Cartas del juzgado y del banco (embargo, concurso, quiebra, rehabilitación) y avisos en pantalla.</summary>
internal sealed class LegalMail
{
    private const string MailAsset = "Data/mail";
    private static readonly LegalNoticeKind[] Letters =
    {
        LegalNoticeKind.EmbargoStarted, LegalNoticeKind.ConcursoOpened, LegalNoticeKind.BankruptcyDeclared,
        LegalNoticeKind.BankruptcyClosed, LegalNoticeKind.Rehabilitated,
    };

    private readonly IModHelper _helper;
    private readonly ModConfig _config;
    private readonly TaxMail _hud;
    private readonly string _prefix;

    public LegalMail(IModHelper helper, IManifest manifest, ModConfig config, TaxMail hud)
    {
        _helper = helper;
        _config = config;
        _hud = hud;
        _prefix = manifest.UniqueID + ".Juzgado.";
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
            IDictionary<string, string> mail = asset.AsDictionary<string, string>().Data;
            foreach (LegalNoticeKind kind in Letters)
            {
                string body = I18n.Get($"legal.mail.{kind}").ToString().Replace("\n", "^");
                mail[_prefix + kind] = $"{body}^^   -{I18n.Get("legal.signature")}[#]{I18n.Get($"legal.mail.{kind}.title")}";
            }
        });
    }

    public void Publish(IEnumerable<LegalNotice> notices)
    {
        foreach (LegalNotice n in notices)
        {
            string amount = Formatting.Money(I18n, n.Amount);
            _hud.Hud(I18n.Get($"legal.hud.{n.Kind}", new { amount, detail = n.Detail ?? "" }),
                n.Kind is LegalNoticeKind.EmbargoStarted or LegalNoticeKind.BankruptcyDeclared or LegalNoticeKind.GoodsSeized
                    or LegalNoticeKind.AccountSeized or LegalNoticeKind.CashSeized or LegalNoticeKind.IncomeGarnished or LegalNoticeKind.SeizureRequested ? 3 : 2);

            if (Letters.Contains(n.Kind) && _config.NewsLetters)
            {
                string key = _prefix + n.Kind;
                Game1.player.mailReceived.Remove(key);
                if (!Game1.player.mailbox.Contains(key))
                {
                    Game1.player.mailbox.Add(key);
                }
            }
        }
    }
}
