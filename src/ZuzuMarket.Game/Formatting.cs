using System.Globalization;
using StardewModdingAPI;

namespace ZuzuMarket.Game;

/// <summary>Formato de números según el idioma del juego (coma decimal en español).</summary>
internal static class Formatting
{
    public static CultureInfo Culture(ITranslationHelper i18n)
    {
        try
        {
            return string.IsNullOrEmpty(i18n.Locale) ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(i18n.Locale);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    /// <summary>0.032 → "3,2%" (o "3.2%" en inglés).</summary>
    public static string Percent(ITranslationHelper i18n, double fraction, bool signed = false)
    {
        string text = (fraction * 100).ToString("0.0", Culture(i18n)) + "%";
        return signed && fraction > 0 ? "+" + text : text;
    }

    public static string Number(ITranslationHelper i18n, double value, string format = "0.000") =>
        value.ToString(format, Culture(i18n));

    public static string Gold(ITranslationHelper i18n, double value) =>
        value.ToString("#,0", Culture(i18n)) + "g";
}
