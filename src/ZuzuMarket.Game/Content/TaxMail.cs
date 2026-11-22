using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using ZuzuMarket.Core.Simulation;
using ZuzuMarket.Core.Taxation;
using ZuzuMarket.Game.Bridges;

namespace ZuzuMarket.Game.Content;

/// <summary>
/// Cartas del fisco: alta, boletas (una carta por jurisdicción y día de emisión), intimaciones, gestión de cobro,
/// recategorización y exclusión del Monotributo. Los textos se generan en <c>Data/mail</c> a partir del estado.
/// </summary>
internal sealed class TaxMail
{
    private const string MailAsset = "Data/mail";
    private const int HudQuestType = 2;
    private const int HudErrorType = 3;

    private readonly IModHelper _helper;
    private readonly ModConfig _config;
    private readonly Func<EconomySimulation?> _getSimulation;
    private readonly string _prefix;

    public TaxMail(IModHelper helper, IManifest manifest, ModConfig config, Func<EconomySimulation?> getSimulation)
    {
        _helper = helper;
        _config = config;
        _getSimulation = getSimulation;
        _prefix = manifest.UniqueID + ".Fisco.";
    }

    private ITranslationHelper I18n => _helper.Translation;

    public void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (!e.NameWithoutLocale.IsEquivalentTo(MailAsset) || _getSimulation() is not { } sim)
        {
            return;
        }

        e.Edit(asset =>
        {
            IDictionary<string, string> mail = asset.AsDictionary<string, string>().Data;
            TaxEngine taxes = sim.Taxes;

            mail[_prefix + "Alta"] = Letter("mail.alta", Jurisdiction.Arf, new { category = taxes.Category });
            mail[_prefix + "Recategorizacion"] = Letter("mail.recategorizacion", Jurisdiction.Arf, new { category = taxes.Category });
            mail[_prefix + "Exclusion"] = Letter("mail.exclusion", Jurisdiction.Arf, new { });

            var recent = taxes.State.Obligations.Where(o => o.IssuedOn.DaysUntil(sim.CurrentDate) <= 56).ToList();
            foreach (var group in recent.GroupBy(o => (o.Jurisdiction, o.IssuedOn)))
            {
                string lines = string.Join("^", group.Select(o => "- " + TaxText.Line(I18n, o)));
                mail[BoletaKey(group.Key.Jurisdiction, group.Key.IssuedOn.AbsoluteDay)] = Letter(
                    "mail.boleta",
                    group.Key.Jurisdiction,
                    new { lines, desk = I18n.Get("mail.desk") });
            }

            foreach (TaxObligation o in taxes.State.Obligations.Where(o => o.Status is ObligationStatus.Intimated or ObligationStatus.InCollection))
            {
                var tokens = new
                {
                    line = TaxText.Line(I18n, o),
                    deadline = o.IntimationDeadline is { } d ? GameDateBridge.Format(I18n, d) : "-",
                    total = Formatting.Gold(I18n, o.Total),
                };
                mail[_prefix + "Intimacion." + o.Id] = Letter("mail.intimacion", o.Jurisdiction, tokens);
                mail[_prefix + "Gestion." + o.Id] = Letter("mail.gestion", o.Jurisdiction, tokens);
            }
        });
    }

    /// <summary>Manda las cartas y avisos de lo que pasó hoy en el fisco.</summary>
    public void Publish(IReadOnlyList<TaxNotice> notices)
    {
        EconomySimulation? sim = _getSimulation();
        if (sim is null || notices.Count == 0)
        {
            return;
        }

        foreach (TaxNotice n in notices)
        {
            TaxObligation? o = n.ObligationId is long id ? sim.Taxes.Find(id) : null;
            switch (n.Kind)
            {
                case TaxNoticeKind.Registered:
                    Send(_prefix + "Alta");
                    break;
                case TaxNoticeKind.Recategorized:
                    Send(_prefix + "Recategorizacion");
                    Hud(I18n.Get("hud.recategorized", new { category = n.Detail }));
                    break;
                case TaxNoticeKind.Excluded:
                    Send(_prefix + "Exclusion");
                    Hud(I18n.Get("hud.excluded"));
                    break;
                case TaxNoticeKind.Overdue when o is not null:
                    Hud(I18n.Get("hud.overdue", new { line = TaxText.Line(I18n, o) }), HudErrorType);
                    break;
                case TaxNoticeKind.Intimated when o is not null:
                    Send(_prefix + "Intimacion." + o.Id);
                    Hud(I18n.Get("hud.intimated", new { tax = TaxText.Tax(I18n, o.Kind) }), HudErrorType);
                    break;
                case TaxNoticeKind.InCollection when o is not null:
                    Send(_prefix + "Gestion." + o.Id);
                    Hud(I18n.Get("hud.in-collection", new { tax = TaxText.Tax(I18n, o.Kind) }), HudErrorType);
                    break;
            }
        }

        // Una carta por jurisdicción con todas las boletas emitidas hoy.
        foreach (var group in notices
            .Where(n => n.Kind == TaxNoticeKind.Issued && n.ObligationId is not null)
            .Select(n => sim.Taxes.Find(n.ObligationId!.Value))
            .OfType<TaxObligation>()
            .GroupBy(o => (o.Jurisdiction, o.IssuedOn)))
        {
            Send(BoletaKey(group.Key.Jurisdiction, group.Key.IssuedOn.AbsoluteDay));
            Hud(I18n.Get("hud.boleta", new
            {
                jurisdiction = TaxText.Jurisdiction(I18n, group.Key.Jurisdiction),
                total = Formatting.Gold(I18n, group.Sum(o => o.Total)),
            }));
        }
    }

    public void Hud(string text, int type = HudQuestType)
    {
        if (_config.NewsHud)
        {
            Game1.addHUDMessage(new HUDMessage(text, type));
        }
    }

    private string BoletaKey(Jurisdiction jurisdiction, int absoluteDay) => $"{_prefix}Boleta.{jurisdiction}.{absoluteDay}";

    private void Send(string key)
    {
        if (_config.NewsLetters && !Game1.player.mailbox.Contains(key))
        {
            Game1.player.mailbox.Add(key);
        }
    }

    private string Letter(string key, Jurisdiction from, object tokens)
    {
        string body = I18n.Get(key, tokens);
        string signature = I18n.Get($"signature.{from}");
        string title = I18n.Get(key + ".title");
        return $"{body.Replace("\n", "^")}^^   -{signature}[#]{title}";
    }
}
