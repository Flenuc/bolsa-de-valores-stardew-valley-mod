using StardewModdingAPI;
using ZuzuMarket.Core.Taxation;
using ZuzuMarket.Game.Bridges;

namespace ZuzuMarket.Game.Content;

/// <summary>Textos de impuestos compartidos por cartas, diálogos y comandos.</summary>
internal static class TaxText
{
    public static string Tax(ITranslationHelper i18n, TaxKind kind) => i18n.Get($"tax.{kind}");

    public static string Jurisdiction(ITranslationHelper i18n, Jurisdiction jurisdiction) => i18n.Get($"jurisdiction.{jurisdiction}");

    public static string Regime(ITranslationHelper i18n, TaxRegime regime) => i18n.Get($"regime.{regime}");

    public static string Status(ITranslationHelper i18n, ObligationStatus status) => i18n.Get($"status.{status}");

    /// <summary>"Monotributo · Primavera del año 1 — 150g (vence Lun 14 de Primavera, año 1)".</summary>
    public static string Line(ITranslationHelper i18n, TaxObligation o) =>
        i18n.Get("tax.line", new
        {
            tax = Tax(i18n, o.Kind),
            period = GameDateBridge.Format(i18n, o.Period),
            amount = Formatting.Gold(i18n, o.Total),
            due = GameDateBridge.Format(i18n, o.DueDate),
        });
}
