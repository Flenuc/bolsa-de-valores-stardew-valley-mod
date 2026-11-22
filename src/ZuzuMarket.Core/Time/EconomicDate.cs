using System.Text.Json.Serialization;

namespace ZuzuMarket.Core.Time;

/// <summary>
/// Fecha del calendario económico: 1 día = 1 rueda, 1 estación (28 días) = 1 período fiscal,
/// 1 año (4 estaciones) = 1 ejercicio fiscal.
/// </summary>
public readonly struct EconomicDate : IEquatable<EconomicDate>, IComparable<EconomicDate>
{
    public const int DaysPerSeason = 28;
    public const int SeasonsPerYear = 4;
    public const int DaysPerYear = DaysPerSeason * SeasonsPerYear;
    public const int DaysPerWeek = 7;

    [JsonConstructor]
    public EconomicDate(int year, Season season, int day)
    {
        if (year < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(year), year, "El año debe ser >= 1.");
        }

        if (!Enum.IsDefined(typeof(Season), season))
        {
            throw new ArgumentOutOfRangeException(nameof(season), season, "Estación inválida.");
        }

        if (day < 1 || day > DaysPerSeason)
        {
            throw new ArgumentOutOfRangeException(nameof(day), day, $"El día debe estar entre 1 y {DaysPerSeason}.");
        }

        Year = year;
        Season = season;
        Day = day;
    }

    public int Year { get; }

    public Season Season { get; }

    public int Day { get; }

    /// <summary>Primer día de la partida: primavera 1 del año 1.</summary>
    public static EconomicDate Start => new(1, Season.Spring, 1);

    /// <summary>Número de días transcurridos desde primavera 1 del año 1 (que es el día 0).</summary>
    [JsonIgnore]
    public int AbsoluteDay => ((Year - 1) * DaysPerYear) + ((int)Season * DaysPerSeason) + (Day - 1);

    [JsonIgnore]
    public Weekday Weekday => (Weekday)((Day - 1) % DaysPerWeek);

    /// <summary>El mercado opera de lunes a viernes; sábados y domingos cierra.</summary>
    [JsonIgnore]
    public bool IsBusinessDay => Weekday < Weekday.Saturday;

    [JsonIgnore]
    public FiscalPeriod FiscalPeriod => new(Year, Season);

    [JsonIgnore]
    public bool IsLastDayOfFiscalPeriod => Day == DaysPerSeason;

    [JsonIgnore]
    public bool IsLastDayOfFiscalYear => Season == Season.Winter && Day == DaysPerSeason;

    public static EconomicDate FromAbsoluteDay(int absoluteDay)
    {
        if (absoluteDay < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(absoluteDay), absoluteDay, "El día absoluto no puede ser negativo.");
        }

        int year = (absoluteDay / DaysPerYear) + 1;
        int dayOfYear = absoluteDay % DaysPerYear;
        var season = (Season)(dayOfYear / DaysPerSeason);
        int day = (dayOfYear % DaysPerSeason) + 1;
        return new EconomicDate(year, season, day);
    }

    public EconomicDate AddDays(int days) => FromAbsoluteDay(AbsoluteDay + days);

    /// <summary>Avanza (o retrocede, si es negativo) la cantidad indicada de días hábiles.</summary>
    /// <remarks>Si la fecha actual no es hábil, el primer paso cae en el día hábil más cercano en esa dirección.</remarks>
    public EconomicDate AddBusinessDays(int businessDays)
    {
        int step = businessDays >= 0 ? 1 : -1;
        int remaining = Math.Abs(businessDays);
        EconomicDate current = this;
        while (remaining > 0)
        {
            current = current.AddDays(step);
            if (current.IsBusinessDay)
            {
                remaining--;
            }
        }

        return current;
    }

    /// <summary>Devuelve esta fecha si es hábil; si no, el próximo día hábil.</summary>
    public EconomicDate NextBusinessDayOrSelf() => IsBusinessDay ? this : AddBusinessDays(1);

    public int DaysUntil(EconomicDate other) => other.AbsoluteDay - AbsoluteDay;

    public bool Equals(EconomicDate other) => Year == other.Year && Season == other.Season && Day == other.Day;

    public override bool Equals(object? obj) => obj is EconomicDate other && Equals(other);

    public override int GetHashCode() => AbsoluteDay;

    public int CompareTo(EconomicDate other) => AbsoluteDay.CompareTo(other.AbsoluteDay);

    public static bool operator ==(EconomicDate left, EconomicDate right) => left.Equals(right);

    public static bool operator !=(EconomicDate left, EconomicDate right) => !left.Equals(right);

    public static bool operator <(EconomicDate left, EconomicDate right) => left.CompareTo(right) < 0;

    public static bool operator >(EconomicDate left, EconomicDate right) => left.CompareTo(right) > 0;

    public static bool operator <=(EconomicDate left, EconomicDate right) => left.CompareTo(right) <= 0;

    public static bool operator >=(EconomicDate left, EconomicDate right) => left.CompareTo(right) >= 0;

    /// <summary>Formato técnico para logs y excepciones. La interfaz usa sus propias traducciones.</summary>
    public override string ToString() => $"{SeasonName(Season)} {Day}, año {Year}";

    internal static string SeasonName(Season season) => season switch
    {
        Season.Spring => "Primavera",
        Season.Summer => "Verano",
        Season.Fall => "Otoño",
        _ => "Invierno",
    };
}
