using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using ZuzuMarket.Core.Macro;
using ZuzuMarket.Core.Markets;
using ZuzuMarket.Core.Simulation;

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

    /// <summary>Decisiones del BCRF y eventos de crédito: van al HUD (los cambios de calificación solo si tenés algo del emisor).</summary>
    public void PublishMacro(EconomySimulation sim, DayStartedInfo info)
    {
        foreach (RateDecision d in info.RateDecisions.Where(d => Math.Abs(d.Change) > 0.00001))
        {
            _hud.Hud(I18n.Get(d.Change > 0 ? "news.rate.up" : "news.rate.down", new { rate = Formatting.Percent(I18n, d.Rate) }));
        }

        foreach (CreditEvent e in info.CreditEvents)
        {
            bool relevant = e.Issuer == CreditEngine.Sovereign
                || sim.Broker.State.Stocks.ContainsKey(e.Issuer)
                || sim.Broker.State.Bonds.Keys.Any(id => sim.Broker.FindBond(id)?.Issuer == e.Issuer);
            if (e.Kind is CreditEventKind.Upgrade or CreditEventKind.Downgrade && !relevant)
            {
                continue;
            }

            string issuer = NewsText.Issuer(I18n, sim, e.Issuer);
            string text = e.Kind switch
            {
                CreditEventKind.Default => I18n.Get("news.default", new { issuer }),
                CreditEventKind.Restructuring => I18n.Get("news.restructuring", new { issuer }),
                CreditEventKind.Upgrade => I18n.Get("news.upgrade", new { issuer, rating = e.Rating }),
                _ => I18n.Get("news.downgrade", new { issuer, rating = e.Rating }),
            };
            _hud.Hud(text, e.Kind is CreditEventKind.Default or CreditEventKind.Downgrade ? 3 : 2);
        }
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
                MarketNoticeKind.Dividend => I18n.Get("broker.hud.dividend", new { ticker = n.Instrument, amount = Formatting.Money(I18n, n.Amount) }),
                MarketNoticeKind.LetraMatured => I18n.Get("broker.hud.letra", new { id = n.Instrument, amount = Formatting.Money(I18n, n.Amount) }),
                MarketNoticeKind.CaucionMatured => I18n.Get("broker.hud.caucion", new { amount = Formatting.Money(I18n, n.Amount) }),
                MarketNoticeKind.Coupon => I18n.Get("broker.hud.coupon", new { id = n.Instrument, amount = Formatting.Money(I18n, n.Amount) }),
                MarketNoticeKind.BondMatured => I18n.Get("broker.hud.bond-matured", new { id = n.Instrument }),
                MarketNoticeKind.MarginTopUp => I18n.Get("broker.hud.margin-topup", new { amount = Formatting.Money(I18n, n.Amount) }),
                MarketNoticeKind.MarginCall => I18n.Get("broker.hud.margin-call", new { amount = Formatting.Money(I18n, n.Amount) }),
                MarketNoticeKind.Liquidation => I18n.Get("broker.hud.liquidation"),
                MarketNoticeKind.FuturesDelivered => I18n.Get("broker.hud.delivered", new { id = n.Instrument, amount = Formatting.Money(I18n, n.Amount) }),
                MarketNoticeKind.FuturesShortfall => n.Amount > 0
                    ? I18n.Get("broker.hud.shortfall", new { id = n.Instrument, units = n.Amount.ToString("#,0") })
                    : I18n.Get("broker.hud.no-cash-delivery", new { id = n.Instrument }),
                MarketNoticeKind.FuturesReceived => I18n.Get("broker.hud.received", new { id = n.Instrument, units = n.Amount.ToString("#,0") }),
                MarketNoticeKind.OptionExercised => I18n.Get("broker.hud.exercised", new { id = n.Instrument }),
                MarketNoticeKind.OptionExpired => I18n.Get("broker.hud.option-expired", new { id = n.Instrument }),
                MarketNoticeKind.RepoRepaid => I18n.Get("broker.hud.repo-repaid", new { amount = Formatting.Money(I18n, n.Amount) }),
                MarketNoticeKind.RepoForcedSale => I18n.Get("broker.hud.repo-forced", new { amount = Formatting.Money(I18n, n.Amount) }),
                MarketNoticeKind.Exchange => I18n.Get("broker.hud.exchange", new { old = n.Instrument.Split('>')[0], @new = n.Instrument.Split('>')[^1], quantity = n.Amount.ToString("#,0") }),
                _ => null,
            };
            if (text is not null)
            {
                bool bad = n.Kind is MarketNoticeKind.MarginCall or MarketNoticeKind.Liquidation or MarketNoticeKind.FuturesShortfall or MarketNoticeKind.RepoForcedSale;
                _hud.Hud(text, bad ? 3 : 2);
            }
        }
    }
}
