using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Tests.Time;

public class EconomicDateTests
{
    [Theory]
    [InlineData(1, Weekday.Monday)]
    [InlineData(5, Weekday.Friday)]
    [InlineData(6, Weekday.Saturday)]
    [InlineData(7, Weekday.Sunday)]
    [InlineData(8, Weekday.Monday)]
    [InlineData(28, Weekday.Sunday)]
    public void Weekday_MatchesGameCalendar(int day, Weekday expected)
    {
        Assert.Equal(expected, new EconomicDate(1, Season.Spring, day).Weekday);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    [InlineData(7, false)]
    [InlineData(13, false)]
    [InlineData(15, true)]
    public void IsBusinessDay_WeekendsAreClosed(int day, bool expected)
    {
        Assert.Equal(expected, new EconomicDate(2, Season.Fall, day).IsBusinessDay);
    }

    [Theory]
    [InlineData(0, 1, Season.Spring, 1)]
    [InlineData(27, 1, Season.Spring, 28)]
    [InlineData(28, 1, Season.Summer, 1)]
    [InlineData(111, 1, Season.Winter, 28)]
    [InlineData(112, 2, Season.Spring, 1)]
    [InlineData(250, 3, Season.Spring, 27)]
    public void AbsoluteDay_RoundTrips(int absolute, int year, Season season, int day)
    {
        var date = new EconomicDate(year, season, day);
        Assert.Equal(absolute, date.AbsoluteDay);
        Assert.Equal(date, EconomicDate.FromAbsoluteDay(absolute));
    }

    [Fact]
    public void AbsoluteDay_RoundTripsForSeveralYears()
    {
        for (int i = 0; i < EconomicDate.DaysPerYear * 5; i++)
        {
            Assert.Equal(i, EconomicDate.FromAbsoluteDay(i).AbsoluteDay);
        }
    }

    [Fact]
    public void AddBusinessDays_SkipsWeekend()
    {
        var friday = new EconomicDate(1, Season.Spring, 5);
        Assert.Equal(new EconomicDate(1, Season.Spring, 8), friday.AddBusinessDays(1));
        Assert.Equal(new EconomicDate(1, Season.Spring, 12), friday.AddBusinessDays(5));
    }

    [Fact]
    public void AddBusinessDays_CrossesSeasonAndYear()
    {
        // Invierno 26 del año 1 es viernes; el próximo día hábil es primavera 1 del año 2 (lunes).
        var friday = new EconomicDate(1, Season.Winter, 26);
        Assert.Equal(Weekday.Friday, friday.Weekday);
        Assert.Equal(new EconomicDate(2, Season.Spring, 1), friday.AddBusinessDays(1));
    }

    [Fact]
    public void AddBusinessDays_FromWeekendAndBackwards()
    {
        var saturday = new EconomicDate(1, Season.Summer, 6);
        Assert.Equal(new EconomicDate(1, Season.Summer, 8), saturday.AddBusinessDays(1));
        Assert.Equal(new EconomicDate(1, Season.Summer, 5), saturday.AddBusinessDays(-1));
        Assert.Equal(saturday, saturday.AddBusinessDays(0));
        Assert.Equal(new EconomicDate(1, Season.Summer, 8), saturday.NextBusinessDayOrSelf());
    }

    [Fact]
    public void FiscalPeriod_IsTheSeason()
    {
        var date = new EconomicDate(3, Season.Fall, 14);
        Assert.Equal(new FiscalPeriod(3, Season.Fall), date.FiscalPeriod);
        Assert.Equal(new FiscalPeriod(3, Season.Winter), date.FiscalPeriod.Next());
        Assert.Equal(new FiscalPeriod(4, Season.Spring), new FiscalPeriod(3, Season.Winter).Next());
        Assert.True(date.FiscalPeriod.Contains(new EconomicDate(3, Season.Fall, 28)));
        Assert.False(date.FiscalPeriod.Contains(new EconomicDate(4, Season.Fall, 1)));
    }

    [Fact]
    public void LastDayFlags()
    {
        Assert.True(new EconomicDate(1, Season.Summer, 28).IsLastDayOfFiscalPeriod);
        Assert.False(new EconomicDate(1, Season.Summer, 28).IsLastDayOfFiscalYear);
        Assert.True(new EconomicDate(1, Season.Winter, 28).IsLastDayOfFiscalYear);
    }

    [Theory]
    [InlineData(0, Season.Spring, 1)]
    [InlineData(1, Season.Spring, 0)]
    [InlineData(1, Season.Spring, 29)]
    public void InvalidDates_Throw(int year, Season season, int day)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EconomicDate(year, season, day));
    }

    [Fact]
    public void Comparison()
    {
        var a = new EconomicDate(1, Season.Winter, 28);
        var b = new EconomicDate(2, Season.Spring, 1);
        Assert.True(a < b);
        Assert.Equal(1, a.DaysUntil(b));
    }

    [Fact]
    public void MarketHours_OpenOnlyOnBusinessDaysDuringTradingHours()
    {
        var monday = new EconomicDate(1, Season.Spring, 1);
        var sunday = new EconomicDate(1, Season.Spring, 7);
        Assert.False(MarketHours.IsOpen(monday, 850));
        Assert.True(MarketHours.IsOpen(monday, 900));
        Assert.True(MarketHours.IsOpen(monday, 1650));
        Assert.False(MarketHours.IsOpen(monday, 1700));
        Assert.False(MarketHours.IsOpen(sunday, 1200));
    }
}
