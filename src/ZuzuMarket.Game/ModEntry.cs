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
            }),
            LogLevel.Debug);

        _store = new SaveDataStore(helper.Data);
        _news = new NewsService(helper, ModManifest, _config, _data, () => Simulation);

        var harmony = new Harmony(ModManifest.UniqueID);
        SellPricePatch.Apply(harmony, Monitor);

        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.GameLoop.DayEnding += OnDayEnding;
        helper.Events.GameLoop.Saving += OnSaving;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        helper.Events.Display.MenuChanged += ShopInflation.OnMenuChanged;
        helper.Events.Content.AssetRequested += _news.OnAssetRequested;

        new ConsoleCommands(helper, Monitor, _config, () => Simulation, ResetSimulation, _news).Register();
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

        DayStartedInfo info = Simulation.OnDayStarted(GameDateBridge.CurrentDate(), WeatherBridge.Today());
        MoneyBridge.Reconcile(Simulation, Helper.Translation.Get("entry.reconcile-day-start"));
        _news!.Publish(info);

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

        int units = ShippingBridge.RecordShipments(Simulation);
        if (units > 0)
        {
            Monitor.Log(Helper.Translation.Get("log.shipments", new { units }), LogLevel.Debug);
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
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        Simulation = null;
        _loadFailed = false;
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
