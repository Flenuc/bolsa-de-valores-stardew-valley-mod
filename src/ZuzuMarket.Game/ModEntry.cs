using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Simulation;
using ZuzuMarket.Core.State;
using ZuzuMarket.Game.Bridges;
using ZuzuMarket.Game.Commands;
using ZuzuMarket.Game.Content;
using ZuzuMarket.Game.Hooks;
using ZuzuMarket.Game.Persistence;
using ZuzuMarket.Game.UI;

namespace ZuzuMarket.Game;

/// <summary>Punto de entrada del mod.</summary>
internal sealed class ModEntry : Mod
{
    private const string DefaultDifficulty = "realista";

    private ModConfig _config = new();
    private GameData? _data;
    private SaveDataStore? _store;
    private SmapiSimulationLog? _log;
    private NewsService? _news;
    private TaxMail? _taxMail;
    private MoneyTracker? _money;
    private Municipalidad? _municipalidad;
    private BrokerMail? _brokerMail;
    private PhoneService? _phone;
    private TerminalActions? _terminalActions;
    private EconomySimulation? _simulation;

    /// <summary>Si el estado guardado estaba dañado, no se guarda nada encima para no perderlo.</summary>
    private bool _loadFailed;

    /// <summary>Simulación de la partida cargada (null en el menú principal o si no se pudo cargar).</summary>
    private EconomySimulation? Simulation
    {
        get => _simulation;
        set
        {
            _simulation = value;
            PriceService.Simulation = value;
        }
    }

    public override void Entry(IModHelper helper)
    {
        _config = helper.ReadConfig<ModConfig>();
        _log = new SmapiSimulationLog(Monitor);

        DataLoadResult result = GameDataLoader.LoadFromDirectory(Path.Combine(helper.DirectoryPath, "data"));
        if (!result.Success)
        {
            foreach (string error in result.Errors)
            {
                Monitor.Log(error, LogLevel.Error);
            }

            Monitor.Log(helper.Translation.Get("log.data-failed"), LogLevel.Error);
            return;
        }

        _data = result.Data!;
        if (_data.FindDifficulty(_config.Difficulty) is null)
        {
            Monitor.Log(helper.Translation.Get("log.unknown-difficulty", new { difficulty = _config.Difficulty, fallback = DefaultDifficulty }), LogLevel.Warn);
            _config.Difficulty = DefaultDifficulty;
        }

        Monitor.Log(
            helper.Translation.Get("log.data-loaded", new
            {
                accounts = _data.Chart.Count,
                commodities = _data.Commodities.Count,
                difficulties = _data.Difficulties.Count,
                events = _data.Events.Count,
                categories = _data.Taxes.Monotributo.Categories.Count,
            }),
            LogLevel.Debug);

        _store = new SaveDataStore(helper.Data);
        _news = new NewsService(helper, ModManifest, _config, _data, () => Simulation);
        _taxMail = new TaxMail(helper, ModManifest, _config, () => Simulation);
        _money = new MoneyTracker(() => Simulation);
        _municipalidad = new Municipalidad(helper, Monitor, () => Simulation, _taxMail, _money);
        _brokerMail = new BrokerMail(helper, ModManifest, _config, _taxMail);
        _phone = new PhoneService(Monitor, _config);
        _terminalActions = new TerminalActions(_brokerMail, _money);
        var objects = new ModObjects(helper.Translation);

        var harmony = new Harmony(ModManifest.UniqueID);
        SellPricePatch.Apply(harmony, Monitor);

        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.GameLoop.DayEnding += OnDayEnding;
        helper.Events.GameLoop.Saving += OnSaving;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        helper.Events.Display.MenuChanged += ShopInflation.OnMenuChanged;
        helper.Events.Content.AssetRequested += _news.OnAssetRequested;
        helper.Events.Content.AssetRequested += _taxMail.OnAssetRequested;
        helper.Events.Content.AssetRequested += BuildingInflation.OnAssetRequested;
        helper.Events.GameLoop.UpdateTicked += _money.OnUpdateTicked;
        helper.Events.Player.InventoryChanged += _money.OnInventoryChanged;
        helper.Events.Input.ButtonPressed += _municipalidad.OnButtonPressed;
        helper.Events.Input.ButtonPressed += OnButtonPressed;
        helper.Events.Content.AssetRequested += objects.OnAssetRequested;
        helper.Events.Content.AssetRequested += _brokerMail.OnAssetRequested;

        new ConsoleCommands(helper, Monitor, _config, () => Simulation, ResetSimulation, _news, _municipalidad, _taxMail, _phone, OpenTerminal).Register();
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        Simulation = null;
        _loadFailed = false;

        if (!Context.IsMainPlayer)
        {
            Monitor.Log(Helper.Translation.Get("log.farmhand"), LogLevel.Warn);
            return;
        }

        try
        {
            WorldState? saved = _store!.Load();
            if (saved is not null)
            {
                Simulation = EconomySimulation.Load(_data!, saved, _log);
                Monitor.Log(Helper.Translation.Get("log.state-loaded", new
                {
                    date = GameDateBridge.Format(Helper.Translation, Simulation.CurrentDate),
                    entries = Simulation.Ledger.Entries.Count,
                }), LogLevel.Info);
            }
            else
            {
                Simulation = CreateSimulation();
                Monitor.Log(Helper.Translation.Get("log.state-created", new { difficulty = Simulation.Difficulty.Name }), LogLevel.Info);
            }
        }
        catch (Exception ex)
        {
            _loadFailed = true;
            Simulation = null;
            Monitor.Log(Helper.Translation.Get("log.state-failed"), LogLevel.Error);
            Monitor.Log(ex.ToString(), LogLevel.Error);
        }
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        if (Simulation is null)
        {
            return;
        }

        DayStartedInfo info = Simulation.OnDayStarted(GameDateBridge.CurrentDate(), WeatherBridge.Today(), FarmBridge.Snapshot());
        MoneyBridge.Reconcile(Simulation, Helper.Translation.Get("entry.reconcile-day-start"));

        // El texto de las cartas y el costo de las construcciones dependen del estado del día.
        Helper.GameContent.InvalidateCache(BuildingInflation.Asset);
        _news!.Publish(info);
        _taxMail!.Publish(info.TaxNotices);
        _brokerMail!.Publish(info.MarketNotices, Simulation.Broker.IsOpen);
        _phone!.Place();
        if (_config.AutoDebit)
        {
            _municipalidad!.AutoDebit(Simulation);
        }

        _money!.Reset();

        if (info.IsNewFiscalPeriod)
        {
            Monitor.Log(Helper.Translation.Get("log.new-period", new { period = GameDateBridge.Format(Helper.Translation, info.Date.FiscalPeriod) }), LogLevel.Debug);
        }
    }

    private void OnDayEnding(object? sender, DayEndingEventArgs e)
    {
        if (Simulation is null)
        {
            return;
        }

        var shipped = ShippingBridge.CollectShipments();
        if (shipped.Count > 0)
        {
            Simulation.RecordShipmentSales(shipped);
            Monitor.Log(Helper.Translation.Get("log.shipments", new { units = shipped.Sum(s => s.Units), gross = shipped.Sum(s => s.Gross) }), LogLevel.Debug);
        }

        Simulation.OnDayEnding();
    }

    private void OnSaving(object? sender, SavingEventArgs e)
    {
        if (Simulation is null || _loadFailed || !Context.IsMainPlayer)
        {
            return;
        }

        MoneyBridge.Reconcile(Simulation, Helper.Translation.Get("entry.reconcile-saving"));
        _store!.Save(Simulation.Snapshot());

        // El teléfono se vuelve a poner a la mañana: así la partida guardada no depende del mod.
        _phone?.Remove();
        _money?.Reset();
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        Simulation = null;
        _loadFailed = false;
    }

    /// <summary>Teléfono de Valle Inversiones o Terminal Bursátil → abre la Terminal.</summary>
    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (Simulation is null || !Context.IsPlayerFree || !e.Button.IsActionButton() || Game1.currentLocation is null)
        {
            return;
        }

        Microsoft.Xna.Framework.Vector2 tile = e.Cursor.GrabTile;
        foreach (Microsoft.Xna.Framework.Vector2 candidate in new[] { tile, tile + new Microsoft.Xna.Framework.Vector2(0, 1) })
        {
            if (Game1.currentLocation.objects.TryGetValue(candidate, out StardewValley.Object? obj) && ModObjects.IsBrokerObject(obj.QualifiedItemId))
            {
                Helper.Input.Suppress(e.Button);
                OpenTerminal();
                return;
            }
        }
    }

    private void OpenTerminal()
    {
        if (Simulation is not null)
        {
            Game1.activeClickableMenu = new TerminalMenu(Helper, Simulation, _terminalActions!);
        }
    }

    private EconomySimulation CreateSimulation() =>
        EconomySimulation.StartNewGame(
            _data!,
            Game1.uniqueIDForThisGame,
            GameDateBridge.CurrentDate(),
            _config.Difficulty,
            MoneyBridge.PlayerGold,
            _log);

    /// <summary>Descarta el estado actual y empieza de cero con la fecha y el oro actuales (solo modo debug).</summary>
    private void ResetSimulation()
    {
        Simulation = CreateSimulation();
        _loadFailed = false;
    }
}
