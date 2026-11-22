using ZuzuMarket.Core.Commodities;
using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Random;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Markets;

/// <summary>
/// Acciones de BYMZ: precio = P/E × UPA × e^sentimiento, donde la UPA sigue la inflación y los precios de los
/// commodities a los que está expuesta cada empresa. Incluye el camino intradiario y el índice MERZU.
/// </summary>
public sealed class StockMarket
{
    public const int OpenMinutes = 9 * 60;
    public const int TickMinutes = 10;

    private readonly GameData _data;
    private readonly MarketsState _s;

    public StockMarket(GameData data, MarketsState state)
    {
        _data = data;
        _s = state;
        foreach (CompanyDefinition company in data.Companies)
        {
            if (!_s.Stocks.ContainsKey(company.Ticker))
            {
                _s.Stocks[company.Ticker] = new StockState();
            }
        }
    }

    public MarketsState State => _s;

    public bool Started => _s.Started;

    public IReadOnlyList<CompanyDefinition> Companies => _data.Companies;

    public double Merzu => _s.Merzu;

    public double MerzuDailyChange => _s.MerzuPrevious > 0 ? (_s.Merzu / _s.MerzuPrevious) - 1 : 0;

    public StockState Get(string ticker) => _s.Stocks[ticker];

    public CompanyDefinition? Find(string ticker) =>
        _data.Companies.FirstOrDefault(c => string.Equals(c.Ticker, ticker, StringComparison.OrdinalIgnoreCase));

    /// <summary>Factor de ingresos: 1 + Σ peso × (precio real promedio del destino − 1).</summary>
    public double RevenueFactor(CompanyDefinition company, CommodityMarket commodities)
    {
        double factor = 1;
        foreach (Exposure exposure in company.Exposures)
        {
            var matching = _data.Commodities.Where(exposure.Matches).ToList();
            if (matching.Count > 0)
            {
                factor += exposure.Weight * (matching.Average(c => commodities.Get(c.Id).RealMultiplier) - 1);
            }
        }

        return Math.Max(0.1, factor);
    }

    public double Eps(CompanyDefinition company, CommodityMarket commodities, double inflationIndex) =>
        company.BaseEps * inflationIndex * RevenueFactor(company, commodities);

    /// <summary>Arranca la bolsa: precios en su valor fundamental, MERZU en su base.</summary>
    public void Start(EconomicDate date, CommodityMarket commodities, double inflationIndex)
    {
        foreach (CompanyDefinition company in _data.Companies)
        {
            StockState s = _s.Stocks[company.Ticker];
            s.Sentiment = 0;
            s.Eps = Eps(company, commodities, inflationIndex);
            s.Close = company.Pe * s.Eps;
            s.PreviousClose = s.Close;
            s.Volume = 0;
            s.Intraday = Flat(s.Close);
            s.History.Add(new StockClose(date, s.Close, 0));
        }

        _s.MerzuBaseCap = Capitalization(c => _s.Stocks[c.Ticker].Close);
        _s.Merzu = _s.MerzuPrevious = _data.Markets.MerzuBase;
        _s.MerzuHistory.Add(new IndexClose(date, _s.Merzu));
        _s.Started = true;
        _s.StartedOn = date;
    }

    /// <summary>Avanza un día. Los fines de semana el mercado no opera y los precios quedan quietos.</summary>
    public void Step(EconomicDate date, CommodityMarket commodities, double inflationIndex, double volatilityMultiplier, ulong seed)
    {
        _s.MerzuPrevious = _s.Merzu;
        foreach (CompanyDefinition company in _data.Companies)
        {
            StockState s = _s.Stocks[company.Ticker];
            s.PreviousClose = s.Close;
            if (!date.IsBusinessDay)
            {
                s.Volume = 0;
                s.Intraday = Flat(s.Close);
                continue;
            }

            DeterministicRandom rng = RandomStreams.For(seed, date, "acciones." + company.Ticker);
            double sigma = company.Volatility * volatilityMultiplier;
            s.Sentiment = ((1 - company.MeanReversion) * s.Sentiment) + (sigma * rng.NextGaussian());
            s.Eps = Eps(company, commodities, inflationIndex);
            s.Close = company.Pe * s.Eps * Math.Exp(s.Sentiment);
            s.Volume = Math.Round(company.AverageVolume * Math.Exp(0.3 * rng.NextGaussian()));
            s.Intraday = Bridge(s.PreviousClose, s.Close, sigma, rng);
            s.History.Add(new StockClose(date, s.Close, s.Volume));
        }

        if (date.IsBusinessDay)
        {
            _s.Merzu = MerzuFor(c => _s.Stocks[c.Ticker].Close);
            _s.MerzuHistory.Add(new IndexClose(date, _s.Merzu));
        }
    }

    /// <summary>Precio vigente a la hora del juego: antes de las 9 la apertura, después de las 17 el cierre.</summary>
    public double PriceAt(string ticker, int timeOfDay)
    {
        StockState s = _s.Stocks[ticker];
        int tick = TickIndex(timeOfDay, s.Intraday.Count);
        return tick < 0 ? s.PreviousClose : s.Intraday[tick];
    }

    public double MerzuAt(int timeOfDay) => MerzuFor(c => PriceAt(c.Ticker, timeOfDay));

    /// <summary>
    /// Golpe al sentimiento (p. ej. cuando la empresa entra en default). Se aplica en el momento: el precio de hoy
    /// (camino intradiario y cierre) se ajusta, y como queda en el sentimiento, también los días siguientes.
    /// </summary>
    public void Shock(string ticker, double sentimentDelta)
    {
        if (!_s.Stocks.TryGetValue(ticker, out StockState? s))
        {
            return;
        }

        double factor = Math.Exp(sentimentDelta);
        s.Sentiment += sentimentDelta;
        s.Close *= factor;
        s.Intraday = s.Intraday.Select(p => p * factor).ToList();
        if (s.History.Count > 0 && s.History[^1].Date == _s.MerzuHistory.LastOrDefault()?.Date)
        {
            s.History[^1] = s.History[^1] with { Close = s.Close };
        }
    }

    /// <summary>Dividendo trimestral por acción (una estación = un trimestre del año).</summary>
    public double QuarterlyDividend(CompanyDefinition company) => _s.Stocks[company.Ticker].Eps / 4 * company.Payout;

    /// <summary>Tick vigente (−1 = todavía no abrió). <paramref name="timeOfDay"/> en formato del juego (1350 = 13:50).</summary>
    internal static int TickIndex(int timeOfDay, int ticks)
    {
        int minutes = ((timeOfDay / 100) * 60) + (timeOfDay % 100) - OpenMinutes;
        int index = (minutes / TickMinutes) - 1;
        return Math.Min(index, ticks - 1);
    }

    private double MerzuFor(Func<CompanyDefinition, double> price) =>
        _s.MerzuBaseCap > 0 ? _data.Markets.MerzuBase * Capitalization(price) / _s.MerzuBaseCap : _data.Markets.MerzuBase;

    private double Capitalization(Func<CompanyDefinition, double> price) =>
        _data.Companies.Sum(c => price(c) * c.Shares);

    private List<double> Flat(double price) => Enumerable.Repeat(price, Math.Max(1, _data.Markets.IntradayTicks)).ToList();

    /// <summary>Puente browniano geométrico de la apertura al cierre: el último tick es exactamente el cierre.</summary>
    private List<double> Bridge(double open, double close, double sigma, DeterministicRandom rng)
    {
        int n = Math.Max(1, _data.Markets.IntradayTicks);
        var walk = new double[n];
        double x = 0;
        double step = sigma / Math.Sqrt(n);
        for (int k = 0; k < n; k++)
        {
            x += step * rng.NextGaussian();
            walk[k] = x;
        }

        double drift = Math.Log(close / open);
        var ticks = new List<double>(n);
        for (int k = 0; k < n; k++)
        {
            double t = (k + 1.0) / n;
            ticks.Add(open * Math.Exp((t * drift) + walk[k] - (t * walk[n - 1])));
        }

        ticks[n - 1] = close;
        return ticks;
    }
}
