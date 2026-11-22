using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Random;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Commodities;

/// <summary>Cierre diario de un commodity (para gráficos e historial).</summary>
public sealed record PricePoint(EconomicDate Date, double Multiplier, double RealMultiplier, double UnitsSold);

/// <summary>Estado serializable de un commodity.</summary>
public sealed class CommodityState
{
    /// <summary>Desvío logarítmico del precio por ruido (proceso con reversión a la media).</summary>
    public double X { get; set; }

    /// <summary>Stock acumulado en el mercado por ventas del jugador (se absorbe con los días).</summary>
    public double Stock { get; set; }

    /// <summary>Unidades vendidas hoy, que entran al stock en el próximo paso.</summary>
    public double PendingSales { get; set; }

    /// <summary>Multiplicador sin inflación (oferta, demanda, estación, eventos).</summary>
    public double RealMultiplier { get; set; } = 1.0;

    /// <summary>Multiplicador final sobre el precio vanilla (incluye inflación).</summary>
    public double Multiplier { get; set; } = 1.0;

    public double PreviousMultiplier { get; set; } = 1.0;

    public List<PricePoint> History { get; set; } = new();

    public double DailyChange => PreviousMultiplier > 0 ? (Multiplier / PreviousMultiplier) - 1.0 : 0;
}

/// <summary>
/// Mercado de commodities: forma los precios a partir de oferta (ventas del jugador), estación, ruido,
/// eventos e inflación (§6.2 del diseño).
/// </summary>
public sealed class CommodityMarket
{
    public const double MinRealMultiplier = 0.30;
    public const double MaxRealMultiplier = 3.00;

    private readonly GameData _data;
    private readonly Dictionary<string, CommodityState> _states;

    public CommodityMarket(GameData data, Dictionary<string, CommodityState> states)
    {
        _data = data;
        _states = states;
        foreach (CommodityDefinition commodity in data.Commodities)
        {
            if (!_states.ContainsKey(commodity.Id))
            {
                _states[commodity.Id] = new CommodityState();
            }
        }
    }

    /// <summary>IPC usado en el último cálculo; es el multiplicador de los objetos que no son commodities.</summary>
    public double CurrentInflationIndex { get; private set; } = 1.0;

    public IReadOnlyDictionary<string, CommodityState> States => _states;

    public CommodityState Get(string commodityId) => _states[commodityId];

    /// <summary>Multiplicador para el precio de venta vanilla de un objeto del juego.</summary>
    public double GetMultiplierForItem(string itemId)
    {
        CommodityDefinition? commodity = _data.FindCommodityByItemId(itemId);
        return commodity is null ? CurrentInflationIndex : _states[commodity.Id].Multiplier;
    }

    /// <summary>Registra unidades vendidas por el jugador. Devuelve false si el objeto no es un commodity.</summary>
    public bool RecordShipment(string itemId, int units)
    {
        CommodityDefinition? commodity = _data.FindCommodityByItemId(itemId);
        if (commodity is null || units <= 0)
        {
            return false;
        }

        _states[commodity.Id].PendingSales += units;
        return true;
    }

    /// <summary>Avanza un día: absorbe stock, mueve el ruido, recalcula precios y guarda el cierre.</summary>
    public void Step(
        EconomicDate date,
        double inflationIndex,
        double volatilityMultiplier,
        ulong seed,
        Func<CommodityDefinition, double> eventImpact)
    {
        CurrentInflationIndex = inflationIndex;
        foreach (CommodityDefinition commodity in _data.Commodities)
        {
            CommodityState state = _states[commodity.Id];
            CommodityMarketParameters p = commodity.Market;
            DeterministicRandom rng = RandomStreams.For(seed, date, "commodity." + commodity.Id);

            double sold = state.PendingSales;
            state.PendingSales = 0;
            state.Stock = (state.Stock * (1.0 - p.StockDecay)) + sold;
            state.X = ((1.0 - p.MeanReversion) * state.X) + (p.Volatility * volatilityMultiplier * rng.NextGaussian());

            state.PreviousMultiplier = state.Multiplier;
            Price(commodity, state, date, inflationIndex, eventImpact(commodity));
            state.History.Add(new PricePoint(date, state.Multiplier, state.RealMultiplier, sold));
        }
    }

    /// <summary>Recalcula los precios del día sin avanzar el tiempo (al empezar la partida o al forzar un evento).</summary>
    public void Recalculate(EconomicDate date, double inflationIndex, Func<CommodityDefinition, double> eventImpact)
    {
        CurrentInflationIndex = inflationIndex;
        foreach (CommodityDefinition commodity in _data.Commodities)
        {
            Price(commodity, _states[commodity.Id], date, inflationIndex, eventImpact(commodity));
        }
    }

    /// <summary>
    /// Multiplicador real esperado en <paramref name="target"/>: el ruido y el stock decaen hacia cero, se aplica el
    /// factor estacional de esa fecha y el impacto de los eventos que sigan activos ese día.
    /// </summary>
    public double ExpectedRealMultiplier(CommodityDefinition commodity, EconomicDate from, EconomicDate target, double eventImpactAtTarget)
    {
        CommodityState state = _states[commodity.Id];
        CommodityMarketParameters p = commodity.Market;
        int days = Math.Max(0, from.DaysUntil(target));
        double x = state.X * Math.Pow(1 - p.MeanReversion, days);
        double stock = (state.Stock + state.PendingSales) * Math.Pow(1 - p.StockDecay, days);
        double log = x - (p.Impact * Math.Log(1 + (stock / p.BaseVolume))) + Math.Log(commodity.SeasonFactor(target.Season)) + eventImpactAtTarget;
        return Math.Clamp(Math.Exp(log), MinRealMultiplier, MaxRealMultiplier);
    }

    private static void Price(CommodityDefinition commodity, CommodityState state, EconomicDate date, double inflationIndex, double eventImpact)
    {
        CommodityMarketParameters p = commodity.Market;
        double supplyPressure = p.Impact * Math.Log(1.0 + (state.Stock / p.BaseVolume));
        double logMultiplier = state.X - supplyPressure + Math.Log(commodity.SeasonFactor(date.Season)) + eventImpact;
        state.RealMultiplier = Math.Clamp(Math.Exp(logMultiplier), MinRealMultiplier, MaxRealMultiplier);
        state.Multiplier = state.RealMultiplier * inflationIndex;
    }
}
