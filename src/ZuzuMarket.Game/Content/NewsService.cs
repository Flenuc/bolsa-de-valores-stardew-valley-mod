using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Macro;
using ZuzuMarket.Core.Simulation;
using ZuzuMarket.Game.Bridges;

namespace ZuzuMarket.Game.Content;

/// <summary>
/// Publica las noticias económicas: aviso en pantalla y cartas del Diario de Zuzu / del INDEF.
/// Las cartas se agregan a <c>Data/mail</c> con el texto de i18n.
/// </summary>
internal sealed class NewsService
{
    private const string MailAsset = "Data/mail";
    private const int HudQuestType = 2;

    private readonly IModHelper _helper;
    private readonly ModConfig _config;
    private readonly GameData _data;
    private readonly Func<EconomySimulation?> _getSimulation;
    private readonly string _mailPrefix;

    public NewsService(IModHelper helper, IManifest manifest, ModConfig config, GameData data, Func<EconomySimulation?> getSimulation)
    {
        _helper = helper;
        _config = config;
        _data = data;
        _getSimulation = getSimulation;
        _mailPrefix = manifest.UniqueID + ".";
    }

    private ITranslationHelper I18n => _helper.Translation;

    private string InflationMailKey => _mailPrefix + "Inflacion";

    private string EventMailKey(string eventId) => _mailPrefix + "Evento." + eventId;

    public void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (!e.NameWithoutLocale.IsEquivalentTo(MailAsset))
        {
            return;
        }

        e.Edit(asset =>
        {
            IDictionary<string, string> mail = asset.AsDictionary<string, string>().Data;
            foreach (EventDefinition ev in _data.Events.Where(ev => ev.Letter))
            {
                mail[EventMailKey(ev.Id)] = Letter(
                    I18n.Get($"event.{ev.Id}.letter"),
                    I18n.Get("news.signature.diario"),
                    I18n.Get($"event.{ev.Id}.headline"));
            }

            EconomySimulation? sim = _getSimulation();
            if (sim is not null && sim.Inflation.State.History.Count > 0)
            {
                InflationRecord last = sim.Inflation.State.History[^1];
                mail[InflationMailKey] = Letter(
                    I18n.Get("news.inflation.letter", new
                    {
                        period = GameDateBridge.Format(I18n, last.Period),
                        rate = Formatting.Percent(I18n, last.Rate),
                        ipc = Formatting.Number(I18n, last.IndexAtClose),
                    }),
                    I18n.Get("news.signature.indef"),
                    I18n.Get("news.inflation.title"));
            }
        });
    }

    /// <summary>Se llama al empezar el día con lo que pasó en la simulación.</summary>
    public void Publish(DayStartedInfo info)
    {
        // El texto de la carta de inflación cambia cada período.
        _helper.GameContent.InvalidateCache(MailAsset);

        foreach (InflationRecord record in info.PublishedInflation)
        {
            Hud(I18n.Get("news.inflation.headline", new
            {
                period = GameDateBridge.Format(I18n, record.Period),
                rate = Formatting.Percent(I18n, record.Rate),
            }));
            SendLetter(InflationMailKey);
        }

        foreach (EventDefinition e in info.NewEvents)
        {
            Announce(e);
        }
    }

    public void Announce(EventDefinition e)
    {
        Hud(I18n.Get($"event.{e.Id}.headline"));
        if (e.Letter)
        {
            SendLetter(EventMailKey(e.Id));
        }
    }

    private void Hud(string text)
    {
        if (_config.NewsHud)
        {
            Game1.addHUDMessage(new HUDMessage(text, HudQuestType));
        }
    }

    private void SendLetter(string key)
    {
        if (_config.NewsLetters && !Game1.player.mailbox.Contains(key))
        {
            Game1.player.mailbox.Add(key);
        }
    }

    /// <summary>Formato de carta del juego: <c>^</c> es salto de línea y <c>[#]</c> separa el título.</summary>
    private static string Letter(string body, string signature, string title) =>
        $"{body.Replace("\n", "^")}^^   -{signature}[#]{title}";
}
