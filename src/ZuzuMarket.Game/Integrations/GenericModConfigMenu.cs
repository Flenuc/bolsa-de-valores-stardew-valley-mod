using StardewModdingAPI;

namespace ZuzuMarket.Game.Integrations;

/// <summary>API de Generic Mod Config Menu (solo los métodos que usa este mod).</summary>
public interface IGenericModConfigMenuApi
{
    void Register(IManifest mod, Action reset, Action save, bool titleScreenOnly = false);

    void AddSectionTitle(IManifest mod, Func<string> text, Func<string>? tooltip = null);

    void AddBoolOption(IManifest mod, Func<bool> getValue, Action<bool> setValue, Func<string> name, Func<string>? tooltip = null, string? fieldId = null);

    void AddNumberOption(IManifest mod, Func<int> getValue, Action<int> setValue, Func<string> name, Func<string>? tooltip = null, int? min = null, int? max = null, int? interval = null, Func<int, string>? formatValue = null, string? fieldId = null);

    void AddTextOption(IManifest mod, Func<string> getValue, Action<string> setValue, Func<string> name, Func<string>? tooltip = null, string[]? allowedValues = null, Func<string, string>? formatAllowedValue = null, string? fieldId = null);
}

/// <summary>Registra las opciones del mod en Generic Mod Config Menu, si está instalado (opcional).</summary>
internal static class GenericModConfigMenu
{
    /// <summary>Vuelve a los valores por defecto sin reemplazar el objeto.</summary>
    private static void Reset(ModConfig config)
    {
        var defaults = new ModConfig();
        foreach (var property in typeof(ModConfig).GetProperties().Where(p => p.CanWrite))
        {
            property.SetValue(config, property.GetValue(defaults));
        }
    }

    private const string Id = "spacechase0.GenericModConfigMenu";

    /// <param name="config">La configuración en uso (se modifica en el lugar: los servicios la tienen referenciada).</param>
    public static void Register(IModHelper helper, IManifest manifest, ModConfig config, Action applied, IReadOnlyDictionary<string, string> difficulties)
    {
        IGenericModConfigMenuApi? api = helper.ModRegistry.GetApi<IGenericModConfigMenuApi>(Id);
        if (api is null)
        {
            return;
        }

        ITranslationHelper i18n = helper.Translation;
        string T(string key) => i18n.Get(key);
        api.Register(manifest, () => Reset(config), () =>
        {
            helper.WriteConfig(config);
            applied();
        });

        api.AddSectionTitle(manifest, () => T("gmcm.section.game"));
        api.AddTextOption(manifest, () => config.Difficulty, v => config.Difficulty = v, () => T("gmcm.difficulty"), () => T("gmcm.difficulty.tip"),
            difficulties.Keys.ToArray(), v => difficulties.TryGetValue(v, out string? name) ? name : v);
        api.AddBoolOption(manifest, () => config.AutoDebit, v => config.AutoDebit = v, () => T("gmcm.autodebit"), () => T("gmcm.autodebit.tip"));
        api.AddBoolOption(manifest, () => config.AllowBuildingAuction, v => config.AllowBuildingAuction = v, () => T("gmcm.auction"), () => T("gmcm.auction.tip"));

        api.AddSectionTitle(manifest, () => T("gmcm.section.news"));
        api.AddBoolOption(manifest, () => config.NewsHud, v => config.NewsHud = v, () => T("gmcm.hud"), () => T("gmcm.hud.tip"));
        api.AddBoolOption(manifest, () => config.NewsLetters, v => config.NewsLetters = v, () => T("gmcm.letters"), () => T("gmcm.letters.tip"));

        api.AddSectionTitle(manifest, () => T("gmcm.section.town"));
        api.AddBoolOption(manifest, () => config.AgencyEnabled, v => config.AgencyEnabled = v, () => T("gmcm.agency"), () => T("gmcm.agency.tip"));
        api.AddBoolOption(manifest, () => config.AgencyPosition is null, v => { if (v) { config.AgencyPosition = null; } }, () => T("gmcm.agency-auto"), () => T("gmcm.agency-auto.tip"));
        api.AddNumberOption(manifest, () => config.PhonePosition.X, v => config.PhonePosition.X = v, () => T("gmcm.phone-x"), null, 0, 200);
        api.AddNumberOption(manifest, () => config.PhonePosition.Y, v => config.PhonePosition.Y = v, () => T("gmcm.phone-y"), null, 0, 200);

        api.AddSectionTitle(manifest, () => T("gmcm.section.debug"));
        api.AddBoolOption(manifest, () => config.DebugMode, v => config.DebugMode = v, () => T("gmcm.debug"), () => T("gmcm.debug.tip"));
    }
}
