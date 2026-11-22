using StardewValley;
using Weather = ZuzuMarket.Core.Data.Weather;

namespace ZuzuMarket.Game.Bridges;

/// <summary>Traduce el clima del día del juego al clima que usa el motor de eventos.</summary>
internal static class WeatherBridge
{
    public static Weather Today()
    {
        if (Game1.isLightning)
        {
            return Weather.Storm;
        }

        if (Game1.isSnowing)
        {
            return Weather.Snow;
        }

        if (Game1.isRaining)
        {
            return Weather.Rain;
        }

        return Game1.isDebrisWeather ? Weather.Wind : Weather.Sun;
    }
}
