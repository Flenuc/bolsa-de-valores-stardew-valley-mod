using System.Text.Json.Serialization;

namespace ZuzuMarket.Core.Time;

/// <summary>Período fiscal mensual (= una estación). El ejercicio fiscal es el año.</summary>
public readonly record struct FiscalPeriod(int Year, Season Season)
{
    [JsonIgnore]
    public int FiscalYear => Year;

    [JsonIgnore]
    public EconomicDate FirstDay => new(Year, Season, 1);

    [JsonIgnore]
    public EconomicDate LastDay => new(Year, Season, EconomicDate.DaysPerSeason);

    public FiscalPeriod Next() => Season == Season.Winter
        ? new FiscalPeriod(Year + 1, Season.Spring)
        : new FiscalPeriod(Year, Season + 1);

    public bool Contains(EconomicDate date) => date.Year == Year && date.Season == Season;

    /// <summary>Período que está <paramref name="seasons"/> estaciones después (o antes, si es negativo).</summary>
    public FiscalPeriod AddSeasons(int seasons)
    {
        int index = ((Year - 1) * EconomicDate.SeasonsPerYear) + (int)Season + seasons;
        return new FiscalPeriod((index / EconomicDate.SeasonsPerYear) + 1, (Season)(index % EconomicDate.SeasonsPerYear));
    }

    /// <summary>Estaciones desde <paramref name="other"/> hasta este período.</summary>
    public int SeasonsSince(FiscalPeriod other) =>
        (((Year - 1) * EconomicDate.SeasonsPerYear) + (int)Season) - (((other.Year - 1) * EconomicDate.SeasonsPerYear) + (int)other.Season);

    public override string ToString() => $"{EconomicDate.SeasonName(Season)} del año {Year}";
}
