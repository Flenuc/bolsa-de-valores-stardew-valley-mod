using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Random;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Events;

/// <summary>Evento en curso. <see cref="End"/> es el último día en que tiene efecto (inclusive).</summary>
public sealed record ActiveEvent(string EventId, EconomicDate Start, EconomicDate End);

public enum NewsKind
{
    /// <summary>Empezó un evento (<see cref="NewsItem.Key"/> = id del evento).</summary>
    Event,

    /// <summary>Se publicó la inflación de un período (<see cref="NewsItem.Value"/> = tasa).</summary>
    Inflation,
}

public sealed record NewsItem(EconomicDate Date, NewsKind Kind, string Key, double Value);

/// <summary>Estado serializable del motor de eventos.</summary>
public sealed class EventsState
{
    public List<ActiveEvent> Active { get; set; } = new();

    /// <summary>Último día de efecto de cada evento (para el cooldown).</summary>
    public Dictionary<string, EconomicDate> LastEnded { get; set; } = new();

    public List<NewsItem> News { get; set; } = new();
}

/// <summary>Sortea, mantiene y vence los eventos económicos (§7 del diseño).</summary>
public sealed class EventEngine
{
    public const int MaxNewEventsPerDay = 2;

    private readonly GameData _data;
    private readonly EventsState _state;

    public EventEngine(GameData data, EventsState state)
    {
        _data = data;
        _state = state;
        _state.Active.RemoveAll(a => data.FindEvent(a.EventId) is null);
    }

    public EventsState State => _state;

    public IReadOnlyList<ActiveEvent> Active => _state.Active;

    public bool IsActive(string eventId) =>
        _state.Active.Any(a => string.Equals(a.EventId, eventId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Saca los eventos que ya terminaron antes de <paramref name="date"/>.</summary>
    public void Expire(EconomicDate date)
    {
        foreach (ActiveEvent finished in _state.Active.Where(a => a.End < date).ToList())
        {
            _state.Active.Remove(finished);
            _state.LastEnded[finished.EventId] = finished.End;
        }
    }

    public bool IsEligible(EventDefinition e, EconomicDate date, Weather weather)
    {
        if (IsActive(e.Id) || date.Year < e.MinYear)
        {
            return false;
        }

        if (_state.LastEnded.TryGetValue(e.Id, out EconomicDate lastEnd) && lastEnd.DaysUntil(date) <= e.CooldownDays)
        {
            return false;
        }

        if (e.FixedDate is { } fixedDate && (fixedDate.Season != date.Season || fixedDate.Day != date.Day))
        {
            return false;
        }

        if (e.Seasons.Count > 0 && !e.Seasons.Contains(date.Season))
        {
            return false;
        }

        return e.Weather.Count == 0 || e.Weather.Contains(weather);
    }

    /// <summary>Sortea los eventos que empiezan hoy.</summary>
    public IReadOnlyList<EventDefinition> Roll(EconomicDate date, Weather weather, DifficultyDefinition difficulty, DeterministicRandom rng)
    {
        var started = new List<EventDefinition>();
        foreach (EventDefinition e in _data.Events)
        {
            // Se sortea un número por evento aunque no sea elegible, para que la secuencia no dependa del estado.
            double roll = rng.NextDouble();
            if (started.Count >= MaxNewEventsPerDay || !IsEligible(e, date, weather))
            {
                continue;
            }

            double probability = e.Probability;
            if (e.Tone == EventTone.Negative)
            {
                probability *= (double)difficulty.NegativeEventFrequency;
            }

            if (roll < probability)
            {
                Start(e, date);
                started.Add(e);
            }
        }

        return started;
    }

    /// <summary>Hace empezar un evento hoy (también lo usa el comando de debug).</summary>
    public ActiveEvent Start(EventDefinition e, EconomicDate date)
    {
        _state.Active.RemoveAll(a => string.Equals(a.EventId, e.Id, StringComparison.OrdinalIgnoreCase));
        var active = new ActiveEvent(e.Id, date, date.AddDays(e.DurationDays - 1));
        _state.Active.Add(active);
        _state.News.Add(new NewsItem(date, NewsKind.Event, e.Id, 0));
        return active;
    }

    public void AddNews(NewsItem item) => _state.News.Add(item);

    /// <summary>Suma del impacto logarítmico de los eventos activos sobre un commodity.</summary>
    public double PriceImpact(CommodityDefinition commodity, EconomicDate date)
    {
        double total = 0;
        foreach (ActiveEvent active in _state.Active)
        {
            EventDefinition? e = _data.FindEvent(active.EventId);
            if (e is null || date < active.Start || date > active.End)
            {
                continue;
            }

            foreach (EventEffect effect in e.Effects)
            {
                if (!effect.Matches(commodity))
                {
                    continue;
                }

                double factor = 1.0;
                if (effect.Profile == ImpactProfile.Decay)
                {
                    factor = 1.0 - ((double)active.Start.DaysUntil(date) / e.DurationDays);
                }

                total += effect.PriceImpact * factor;
            }
        }

        return total;
    }
}
