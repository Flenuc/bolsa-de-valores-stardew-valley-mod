using System.Globalization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.Characters;
using ZuzuMarket.Core.Commodities;
using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Simulation;
using ZuzuMarket.Core.Taxation;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Game.Content;

/// <summary>
/// Julián Ferreyra, agente de bolsa de Valle Inversiones: atiende en la Agencia, comenta el mercado y tu cartera,
/// recibe regalos y, con la amistad, te baja las comisiones y te pasa datos.
/// </summary>
internal sealed class JulianNpc
{
    public const string Name = "ZuzuMarket.Julian";
    private const string CharactersAsset = "Characters/" + Name;
    private const string PortraitAsset = "Portraits/" + Name;
    private const string ScheduleAsset = "Characters/schedules/" + Name;
    private const string DialogueAsset = "Characters/Dialogue/" + Name;
    private const string EventsAsset = "Data/Events/" + AgencyMap.LocationName;
    private const string TipMail = "Flenuc.ZuzuMarket.JulianTip";

    /// <summary>Comisión: descuento por corazones (4, 6 y 8).</summary>
    private static readonly (int Hearts, decimal Discount)[] Discounts = { (8, 0.40m), (6, 0.25m), (4, 0.10m) };

    /// <summary>Tiles del Saloon donde se sienta los viernes (se usa el primero que esté libre).</summary>
    private static readonly Point[] SaloonSpots = { new(14, 19), new(20, 19), new(10, 18), new(33, 18) };

    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly ModConfig _config;
    private readonly AgencyExterior _exterior;
    private readonly Func<EconomySimulation?> _getSimulation;
    private string _tip = "";

    public JulianNpc(IModHelper helper, IMonitor monitor, ModConfig config, AgencyExterior exterior, Func<EconomySimulation?> getSimulation)
    {
        _helper = helper;
        _monitor = monitor;
        _config = config;
        _exterior = exterior;
        _getSimulation = getSimulation;
    }

    private ITranslationHelper I18n => _helper.Translation;

    public static NPC? Npc => Game1.getCharacterFromName(Name);

    public static int Hearts => Game1.player?.friendshipData.TryGetValue(Name, out Friendship? f) == true ? f.Points / 250 : 0;

    /// <summary>Descuento de comisiones de la ALyC por la amistad.</summary>
    public static decimal CommissionDiscount => Discounts.FirstOrDefault(d => Hearts >= d.Hearts).Discount;

    // ───────────────────────────── Assets ─────────────────────────────

    public void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (!_config.AgencyEnabled)
        {
            return;
        }

        if (e.NameWithoutLocale.IsEquivalentTo("Data/Characters"))
        {
            e.Edit(asset =>
            {
                AgencyLayout layout = AgencyMap.EnsureLayout();
                asset.AsDictionary<string, CharacterData>().Data[Name] = new CharacterData
                {
                    DisplayName = I18n.Get("julian.name"),
                    Gender = Gender.Male,
                    Age = NpcAge.Adult,
                    Manner = NpcManner.Polite,
                    SocialAnxiety = NpcSocialAnxiety.Outgoing,
                    Optimism = NpcOptimism.Positive,
                    BirthSeason = StardewValley.Season.Fall,
                    BirthDay = 12,
                    HomeRegion = "Town",
                    CanBeRomanced = false,
                    Calendar = CalendarBehavior.AlwaysShown,
                    SocialTab = SocialTabBehavior.AlwaysShown,
                    Home = new List<CharacterHomeData>
                    {
                        new() { Id = "Default", Location = AgencyMap.LocationName, Tile = layout.Julian, Direction = "down" },
                    },
                };
            });
        }
        else if (e.NameWithoutLocale.IsEquivalentTo(CharactersAsset))
        {
            e.LoadFromModFile<Texture2D>("assets/julian.png", AssetLoadPriority.Exclusive);
        }
        else if (e.NameWithoutLocale.IsEquivalentTo(PortraitAsset))
        {
            e.LoadFromModFile<Texture2D>("assets/julian_portrait.png", AssetLoadPriority.Exclusive);
        }
        else if (e.NameWithoutLocale.IsEquivalentTo(ScheduleAsset))
        {
            e.LoadFrom(BuildSchedule, AssetLoadPriority.Exclusive);
        }
        else if (e.NameWithoutLocale.IsEquivalentTo(DialogueAsset))
        {
            e.LoadFrom(BuildDialogue, AssetLoadPriority.Exclusive);
        }
        else if (e.NameWithoutLocale.IsEquivalentTo("Data/NPCGiftTastes"))
        {
            e.Edit(asset => asset.AsDictionary<string, string>().Data[Name] = string.Join('/',
                I18n.Get("julian.gift.love"), "395 348 72 336 337 430",
                I18n.Get("julian.gift.like"), "424 346 459 303 66 62",
                I18n.Get("julian.gift.dislike"), "330 92 771",
                I18n.Get("julian.gift.hate"), "168 169 170 171 172 167",
                I18n.Get("julian.gift.neutral"), ""));
        }
        else if (e.NameWithoutLocale.IsEquivalentTo(EventsAsset))
        {
            e.LoadFrom(BuildEvents, AssetLoadPriority.Exclusive);
        }
        else if (e.NameWithoutLocale.IsEquivalentTo("Data/mail"))
        {
            e.Edit(asset => asset.AsDictionary<string, string>().Data[TipMail] =
                $"{_tip.Replace("\n", "^")}^^   -{I18n.Get("julian.name")}[#]{I18n.Get("julian.tip.title")}");
        }
    }

    private Dictionary<string, string> BuildSchedule()
    {
        AgencyLayout layout = AgencyMap.EnsureLayout();
        string desk = $"{AgencyMap.LocationName} {layout.Julian.X} {layout.Julian.Y} 2";
        var schedule = new Dictionary<string, string>
        {
            ["default"] = $"800 {desk}",
            ["rain"] = $"800 {desk}",
            ["Sun"] = $"900 {AgencyMap.LocationName} {layout.Julian.X - 1} {layout.Julian.Y} 2",
        };

        // Solo sale si la fachada quedó en el pueblo (si no, no tiene por dónde volver).
        if (_exterior.Exit is { } exit)
        {
            if (FreeSaloonSpot() is { } spot)
            {
                schedule["Fri"] = $"800 {desk}/1800 Saloon {spot.X} {spot.Y} 2/2330 {desk}";
            }

            // Al costado de la puerta (dentro del margen libre que se verificó al poner la fachada).
            schedule["Sat"] = $"1000 Town {exit.X + 2} {exit.Y} 2/1400 {desk}";
        }

        return schedule;
    }

    private static Point? FreeSaloonSpot()
    {
        GameLocation? saloon = Game1.getLocationFromName("Saloon");
        return saloon is null ? null : SaloonSpots.Cast<Point?>().FirstOrDefault(p => saloon.isTilePassable(new xTile.Dimensions.Location(p!.Value.X, p.Value.Y), Game1.viewport));
    }

    private Dictionary<string, string> BuildDialogue()
    {
        var dialogue = new Dictionary<string, string> { ["Introduction"] = I18n.Get("julian.dialogue.intro") };
        foreach (string day in new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" })
        {
            dialogue[day] = I18n.Get($"julian.dialogue.{day}");
            dialogue[day + "6"] = I18n.Get($"julian.dialogue.{day}6");
        }

        foreach (string key in new[] { "AcceptGift_(O)395", "AcceptBirthdayGift_Positive", "AcceptBirthdayGift_Negative" })
        {
            dialogue[key] = I18n.Get($"julian.dialogue.{key.Replace("(O)", "")}");
        }

        return dialogue;
    }

    private Dictionary<string, string> BuildEvents()
    {
        AgencyLayout l = AgencyMap.EnsureLayout();
        string setup = $"none/{l.Arrival.X} {l.Arrival.Y}/farmer {l.Arrival.X} {l.Arrival.Y} 0 {Name} {l.Julian.X} {l.Julian.Y} 2/skippable/pause 500";
        string Speak(string key) => $"speak {Name} \"{I18n.Get(key).ToString().Replace("\"", "'")}\"";
        return new Dictionary<string, string>
        {
            [$"{Name}_2/f {Name} 500/t 900 1700"] = $"{setup}/{Speak("julian.event2.a")}/pause 300/{Speak("julian.event2.b")}/pause 300/{Speak("julian.event2.c")}/end",
            [$"{Name}_4/f {Name} 1000/t 900 1700"] = $"{setup}/emote {Name} 28/{Speak("julian.event4.a")}/pause 300/{Speak("julian.event4.b")}/pause 300/{Speak("julian.event4.c")}/end",
            [$"{Name}_6/f {Name} 1500/t 900 1700"] = $"{setup}/emote {Name} 32/{Speak("julian.event6.a")}/pause 300/{Speak("julian.event6.b")}/pause 300/{Speak("julian.event6.c")}/end",
        };
    }

    // ───────────────────────────── Día a día ─────────────────────────────

    /// <summary>Cada mañana: la línea del día sobre el mercado y, los lunes con 6 corazones, la carta con un dato.</summary>
    public void OnDayStarted(EconomySimulation sim)
    {
        if (!_config.AgencyEnabled || Npc is not { } npc)
        {
            return;
        }

        string line = MarketLine(sim);
        if (line.Length > 0)
        {
            npc.CurrentDialogue.Push(new Dialogue(npc, $"{Name}:market", line));
        }

        if (sim.CurrentDate.Day % 7 == 1 && Hearts >= 6)
        {
            _tip = Tip(sim);
            _helper.GameContent.InvalidateCache("Data/mail");
            Game1.player.mailReceived.Remove(TipMail);
            if (!Game1.player.mailbox.Contains(TipMail))
            {
                Game1.player.mailbox.Add(TipMail);
            }
        }
    }

    /// <summary>Comentario del día: el mercado, tu cartera, tus deudas o tus sociedades.</summary>
    private string MarketLine(EconomySimulation sim)
    {
        var lines = new List<string>();
        double merzu = sim.Stocks.MerzuDailyChange;
        lines.Add(I18n.Get(merzu > 0.01 ? "julian.market.up" : merzu < -0.01 ? "julian.market.down" : "julian.market.flat",
            new { merzu = Formatting.Percent(I18n, merzu), rate = Formatting.Percent(I18n, sim.CentralBank.PolicyRate) }));

        if (sim.Legal.IsEmbargoActive)
        {
            lines.Add(I18n.Get("julian.market.embargo"));
        }
        else if (sim.Taxes.Unpaid.Any(o => o.DueDate < sim.CurrentDate))
        {
            lines.Add(I18n.Get("julian.market.overdue"));
        }
        else if (sim.Broker.IsOpen)
        {
            decimal value = sim.Broker.PortfolioValue(sim.CurrentDate, Game1.timeOfDay);
            lines.Add(I18n.Get("julian.market.portfolio", new { value = Formatting.Money(I18n, value) }));
        }
        else
        {
            lines.Add(I18n.Get("julian.market.no-account"));
        }

        if (sim.Companies.Active.FirstOrDefault(c => c.Listing is not null) is { } listed)
        {
            lines.Add(I18n.Get("julian.market.listed", new { ticker = listed.Ticker, price = Formatting.Money(I18n, sim.Companies.MarketPrice(listed)) }));
        }

        return string.Join("#$b#", lines);
    }

    /// <summary>El dato de la semana: el commodity con la mayor suba o baja esperada en 7 días.</summary>
    private string Tip(EconomySimulation sim)
    {
        EconomicDate today = sim.CurrentDate;
        EconomicDate target = today.AddDays(7);
        (CommodityDefinition Commodity, double Change)? best = null;
        foreach (CommodityDefinition c in sim.Data.Commodities)
        {
            double now = sim.Market.Get(c.Id).RealMultiplier;
            double expected = sim.Market.ExpectedRealMultiplier(c, today, target, sim.Events.PriceImpact(c, target));
            double change = now > 0 ? (expected / now) - 1 : 0;
            if (best is null || Math.Abs(change) > Math.Abs(best.Value.Change))
            {
                best = (c, change);
            }
        }

        return best is { } b
            ? I18n.Get(b.Change >= 0 ? "julian.tip.up" : "julian.tip.down", new { name = b.Commodity.Name, change = Formatting.Percent(I18n, Math.Abs(b.Change)) })
            : I18n.Get("julian.tip.none");
    }

    /// <summary>
    /// Si ya hablaron hoy, al tocar a Julián se ofrece abrir la Terminal o pagar boletas. Devuelve true si atendió.
    /// </summary>
    public bool TryServe(ButtonPressedEventArgs e, Action openTerminal, Action payBills)
    {
        if (!_config.AgencyEnabled || !e.Button.IsActionButton() || Npc is not { } npc || npc.currentLocation != Game1.currentLocation
            || npc.CurrentDialogue.Count > 0 || Game1.player.ActiveObject is not null)
        {
            return false;
        }

        Vector2 grab = e.Cursor.GrabTile;
        if (Math.Abs(npc.Tile.X - grab.X) > 0.5f || (Math.Abs(npc.Tile.Y - grab.Y) > 0.5f && Math.Abs(npc.Tile.Y - 1 - grab.Y) > 0.5f))
        {
            return false;
        }

        if (!Game1.player.friendshipData.TryGetValue(Name, out Friendship? f) || !f.TalkedToToday)
        {
            return false;
        }

        _helper.Input.Suppress(e.Button);
        Game1.currentLocation.createQuestionDialogue(
            I18n.Get("julian.serve.question"),
            new[]
            {
                new Response("terminal", I18n.Get("julian.serve.terminal")),
                new Response("pay", I18n.Get("julian.serve.pay")),
                new Response("bye", I18n.Get("julian.serve.bye")),
            },
            (_, answer) =>
            {
                if (answer == "terminal")
                {
                    openTerminal();
                }
                else if (answer == "pay")
                {
                    payBills();
                }
            });
        return true;
    }

    /// <summary>Para el comando de diagnóstico.</summary>
    public string Status() => Npc is { } npc
        ? string.Create(CultureInfo.InvariantCulture, $"{npc.displayName} en {npc.currentLocation?.Name} ({npc.Tile.X}, {npc.Tile.Y}) · {Hearts} corazones · comisión −{CommissionDiscount:P0}")
        : "Julián no está cargado";
}
