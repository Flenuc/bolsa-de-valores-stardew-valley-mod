namespace ZuzuMarket.Core.Time;

/// <summary>
/// Horario del mercado. Las horas usan el formato del juego: 900 = 9:00, 1730 = 17:30.
/// </summary>
public static class MarketHours
{
    public const int Open = 900;
    public const int Close = 1700;

    /// <summary>Los precios intradiarios se actualizan cada 10 minutos del juego.</summary>
    public const int TickMinutes = 10;

    public static bool IsOpen(EconomicDate date, int timeOfDay) =>
        date.IsBusinessDay && timeOfDay >= Open && timeOfDay < Close;
}
