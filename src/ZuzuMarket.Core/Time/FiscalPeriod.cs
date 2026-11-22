namespace ZuzuMarket.Core.Time;

/// <summary>Período fiscal mensual (= una estación). El ejercicio fiscal es el año.</summary>
public readonly record struct FiscalPeriod(int Year, Season Season)
{
    public int FiscalYear => Year;

    public EconomicDate FirstDay => new(Year, Season, 1);

    public EconomicDate LastDay => new(Year, Season, EconomicDate.DaysPerSeason);

    public FiscalPeriod Next() => Season == Season.Winter
        ? new FiscalPeriod(Year + 1, Season.Spring)
        : new FiscalPeriod(Year, Season + 1);

    public bool Contains(EconomicDate date) => date.Year == Year && date.Season == Season;

    public override string ToString() => $"{EconomicDate.SeasonName(Season)} del año {Year}";
}
