using StardewModdingAPI;
using StardewValley;
using ZuzuMarket.Game.Compat;
using ZuzuMarket.Game.Content;

namespace ZuzuMarket.Game.Commands;

/// <summary>Comandos de la Agencia y de compatibilidad (zuzu_compat, zuzu_agency).</summary>
internal sealed class AgencyCommands
{
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly ModConfig _config;
    private readonly ModCompat _compat;
    private readonly AgencyExterior _exterior;
    private readonly Agency _agency;
    private readonly JulianNpc _julian;
    private readonly PhoneService _phone;

    public AgencyCommands(IModHelper helper, IMonitor monitor, ModConfig config, ModCompat compat, AgencyExterior exterior, Agency agency, JulianNpc julian, PhoneService phone)
    {
        _helper = helper;
        _monitor = monitor;
        _config = config;
        _compat = compat;
        _exterior = exterior;
        _agency = agency;
        _julian = julian;
        _phone = phone;
    }

    private ITranslationHelper I18n => _helper.Translation;

    public void Register()
    {
        _helper.ConsoleCommands.Add("zuzu_compat", I18n.Get("cmd.compat.help"), (_, _) => Compat());
        _helper.ConsoleCommands.Add("zuzu_agency", I18n.Get("cmd.agency.help"), (_, args) => AgencyCmd(args));
    }

    private void Compat()
    {
        string agency = _exterior.Placement is { } p
            ? I18n.Get("cmd.compat.agency-at", new { x = p.X, y = p.Y, door = $"{_exterior.Door!.Value.X}, {_exterior.Door.Value.Y}" })
            : I18n.Get("cmd.compat.agency-none", new { problem = _exterior.Problem ?? (_config.AgencyEnabled ? "-" : I18n.Get("cmd.compat.disabled")) });
        string phone = _phone.PlacedAt is { } t ? $"{t.X}, {t.Y}" : I18n.Get("cmd.compat.phone-none");
        _monitor.Log(I18n.Get("cmd.compat.summary", new
        {
            variant = _compat.Variant,
            agency,
            phone,
            julian = _julian.Status(),
            desk = Game1.getLocationFromName("ManorHouse") is null ? I18n.Get("cmd.compat.no-desk") : "ManorHouse",
        }), LogLevel.Info);
    }

    private void AgencyCmd(string[] args)
    {
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "info";
        if (mode == "info")
        {
            Compat();
            return;
        }

        if (!Context.IsWorldReady)
        {
            return;
        }

        if (mode == "here")
        {
            if (Game1.currentLocation?.Name != "Town")
            {
                _monitor.Log(I18n.Get("cmd.agency.not-town"), LogLevel.Warn);
                return;
            }

            // La fachada queda con la puerta justo arriba del jugador.
            var tile = Game1.player.TilePoint;
            _config.AgencyPosition = new TilePosition { X = tile.X - 1, Y = tile.Y - 4 };
        }
        else if (mode == "auto")
        {
            _config.AgencyPosition = null;
        }
        else
        {
            _monitor.Log(I18n.Get("cmd.agency.usage"), LogLevel.Info);
            return;
        }

        _helper.WriteConfig(_config);
        _exterior.Reload();
        _agency.Reload();
        _helper.GameContent.InvalidateCache("Characters/schedules/" + JulianNpc.Name);
        Compat();
    }
}
