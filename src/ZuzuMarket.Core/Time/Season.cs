namespace ZuzuMarket.Core.Time;

/// <summary>Estación del juego. El orden coincide con <c>Game1.seasonIndex</c>.</summary>
public enum Season
{
    Spring = 0,
    Summer = 1,
    Fall = 2,
    Winter = 3,
}

/// <summary>Día de la semana. En Stardew Valley el día 1 de cada estación es lunes.</summary>
public enum Weekday
{
    Monday = 0,
    Tuesday = 1,
    Wednesday = 2,
    Thursday = 3,
    Friday = 4,
    Saturday = 5,
    Sunday = 6,
}
