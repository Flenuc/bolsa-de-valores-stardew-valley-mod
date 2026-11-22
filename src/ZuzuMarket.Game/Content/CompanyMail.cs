using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using ZuzuMarket.Core.Companies;

namespace ZuzuMarket.Game.Content;

/// <summary>Cartas de la Inspección de Personas Jurídicas y avisos de tus sociedades.</summary>
internal sealed class CompanyMail
{
    private const string MailAsset = "Data/mail";
    private static readonly CompanyNoticeKind[] Letters =
    {
        CompanyNoticeKind.Created, CompanyNoticeKind.Transformed, CompanyNoticeKind.Dissolved, CompanyNoticeKind.Bankrupt,
        CompanyNoticeKind.Listed, CompanyNoticeKind.Delisted, CompanyNoticeKind.BondsIssued,
    };

    private readonly IModHelper _helper;
    private readonly ModConfig _config;
    private readonly TaxMail _hud;
    private readonly string _prefix;

    public CompanyMail(IModHelper helper, IManifest manifest, ModConfig config, TaxMail hud)
    {
        _helper = helper;
        _config = config;
        _hud = hud;
        _prefix = manifest.UniqueID + ".Ipj.";
    }

    private ITranslationHelper I18n => _helper.Translation;

    public static string TypeName(ITranslationHelper i18n, string type) => i18n.Get($"company.type.{type}");

    public string Name(CompanyState c) => I18n.Get("company.name", new { type = TypeName(I18n, c.Type), form = I18n.Get($"company.form.{c.Form}") });

    public void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (!e.NameWithoutLocale.IsEquivalentTo(MailAsset))
        {
            return;
        }

        e.Edit(asset =>
        {
            IDictionary<string, string> mail = asset.AsDictionary<string, string>().Data;
            foreach (CompanyNoticeKind kind in Letters)
            {
                string body = I18n.Get($"company.mail.{kind}").ToString().Replace("\n", "^");
                mail[_prefix + kind] = $"{body}^^   -{I18n.Get("company.signature")}[#]{I18n.Get($"company.mail.{kind}.title")}";
            }
        });
    }

    public void Publish(IEnumerable<CompanyNotice> notices, Func<string, CompanyState?>? find = null)
    {
        foreach (CompanyNotice n in notices)
        {
            string name = find?.Invoke(n.Company) is { } c ? Name(c) : n.Company;
            string detail = n.Kind is CompanyNoticeKind.Created ? TypeName(I18n, n.Detail ?? "") : n.Detail ?? "";
            bool bad = n.Kind is CompanyNoticeKind.TaxesOverdue or CompanyNoticeKind.InCollection or CompanyNoticeKind.Bankrupt;
            _hud.Hud(I18n.Get($"company.hud.{n.Kind}", new { name, amount = Formatting.Money(I18n, n.Amount), detail }), bad ? 3 : 2);

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
