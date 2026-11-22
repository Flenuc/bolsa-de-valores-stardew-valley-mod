using StardewModdingAPI;
using StardewValley;
using ZuzuMarket.Core.Time;
using Season = ZuzuMarket.Core.Time.Season;

namespace ZuzuMarket.Game.Bridges;

/// <summary>Traduce la fecha del juego al calendario económico y la formatea para mostrarla.</summary>
internal static class GameDateBridge
{
    public static EconomicDate CurrentDate() => new(
        Math.Max(1, Game1.year),
        (Season)Math.Clamp(Game1.seasonIndex, 0, 3),
        Math.Clamp(Game1.dayOfMonth, 1, EconomicDate.DaysPerSeason));

    public static string SeasonName(ITranslationHelper i18n, Season season) =>
        i18n.Get($"season.{season.ToString().ToLowerInvariant()}");

    public static string WeekdayName(ITranslationHelper i18n, Weekday weekday) =>
        i18n.Get($"weekday.{weekday.ToString().ToLowerInvariant()}");

    public static string Format(ITranslationHelper i18n, EconomicDate date) =>
        i18n.Get("date.format", new
        {
            weekday = WeekdayName(i18n, date.Weekday),
            day = date.Day,
            season = SeasonName(i18n, date.Season),
            year = date.Year,
        });

    public static string Format(ITranslationHelper i18n, FiscalPeriod period) =>
        i18n.Get("period.format", new { season = SeasonName(i18n, period.Season), year = period.Year });
}
