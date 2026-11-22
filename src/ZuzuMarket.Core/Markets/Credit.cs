using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Random;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Markets;

public sealed class RatingLevel
{
    public string Id { get; set; } = "";

    /// <summary>Spread mensual sobre la tasa de política.</summary>
    public double Spread { get; set; }

    /// <summary>Probabilidad anual de default.</summary>
    public double AnnualDefault { get; set; }
}

/// <summary>Calificaciones crediticias (<c>data/ratings.json</c>).</summary>
public sealed class RatingRules
{
    /// <summary>De la mejor a la peor; la última es "D" (default).</summary>
    public List<RatingLevel> Scale { get; set; } = new();

    /// <summary>Probabilidad por estación de subir o bajar un escalón.</summary>
    public double UpgradeProbability { get; set; } = 0.05;

    public double DowngradeProbability { get; set; } = 0.05;

    public Dictionary<string, string> SovereignByDifficulty { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Companies { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public double SovereignRecovery { get; set; } = 0.30;

    public double CorporateRecovery { get; set; } = 0.40;

    /// <summary>Estaciones entre el default y el canje.</summary>
    public int RestructuringDelay { get; set; } = 2;

    /// <summary>Calificación después del canje (soberano / empresas).</summary>
    public string SovereignAfterRestructuring { get; set; } = "CCC";

    public string CorporateAfterRestructuring { get; set; } = "B";

    public int IndexOf(string rating) => Scale.FindIndex(r => string.Equals(r.Id, rating, StringComparison.OrdinalIgnoreCase));

    public RatingLevel Level(string rating) => Scale[Math.Max(0, IndexOf(rating))];

    public string DefaultRating => Scale.Count > 0 ? Scale[^1].Id : "D";
}

public sealed class IssuerState
{
    /// <summary>Sociedad del jugador: su calificación la fija su balance y su default, la falta de pago.</summary>
    public bool Player { get; set; }

    public string Rating { get; set; } = "";

    public bool Defaulted { get; set; }

    public FiscalPeriod? DefaultedIn { get; set; }
}

public enum CreditEventKind
{
    Upgrade,
    Downgrade,
    Default,
    Restructuring,
}

public sealed record CreditEvent(CreditEventKind Kind, string Issuer, string Rating);

/// <summary>Emisores (el soberano y las empresas), sus calificaciones y sus defaults.</summary>
public sealed class CreditEngine
{
    public const string Sovereign = "FERNGILL";

    private readonly GameData _data;
    private readonly Dictionary<string, IssuerState> _issuers;

    public CreditEngine(GameData data, Dictionary<string, IssuerState> issuers)
    {
        _data = data;
        _issuers = issuers;
    }

    public RatingRules Rules => _data.Ratings;

    public IReadOnlyDictionary<string, IssuerState> Issuers => _issuers;

    public IssuerState Get(string issuer) => _issuers[issuer];

    public bool IsDefaulted(string issuer) => _issuers.TryGetValue(issuer, out IssuerState? s) && s.Defaulted;

    public double Spread(string issuer) =>
        _issuers.TryGetValue(issuer, out IssuerState? s) && !s.Defaulted ? Rules.Level(s.Rating).Spread : Rules.Scale.Count > 0 ? Rules.Scale[^2].Spread : 0;

    /// <summary>Riesgo país en puntos básicos anuales (spread mensual del soberano × 4 estaciones).</summary>
    public double CountryRiskBps => Spread(Sovereign) * EconomicDate.SeasonsPerYear * 10_000;

    public double Recovery(string issuer) =>
        issuer == Sovereign ? Rules.SovereignRecovery : _issuers.TryGetValue(issuer, out IssuerState? s) && s.Player ? 0 : Rules.CorporateRecovery;

    /// <summary>Califica a todos los emisores que todavía no tienen calificación.</summary>
    public void Initialize(DifficultyDefinition difficulty)
    {
        if (!_issuers.ContainsKey(Sovereign))
        {
            _issuers[Sovereign] = new IssuerState
            {
                Rating = Rules.SovereignByDifficulty.TryGetValue(difficulty.Id, out string? r) ? r : "B",
            };
        }

        foreach (CompanyDefinition company in _data.Companies)
        {
            if (!_issuers.ContainsKey(company.Ticker))
            {
                _issuers[company.Ticker] = new IssuerState
                {
                    Rating = Rules.Companies.TryGetValue(company.Ticker, out string? r) ? r : "BB",
                };
            }
        }
    }

    /// <summary>Probabilidad de default en una estación.</summary>
    public double SeasonalDefaultProbability(string issuer, DifficultyDefinition difficulty)
    {
        IssuerState s = _issuers[issuer];
        double annual = issuer == Sovereign
            ? (double)difficulty.SovereignDefaultProbability * (1 + Math.Max(0, Rules.IndexOf(s.Rating) - Rules.IndexOf("B")) * 0.5)
            : Rules.Level(s.Rating).AnnualDefault * (double)difficulty.NegativeEventFrequency;
        annual = Math.Clamp(annual, 0, 1);
        return 1 - Math.Pow(1 - annual, 1.0 / EconomicDate.SeasonsPerYear);
    }

    /// <summary>Cada estación: migraciones de calificación y defaults.</summary>
    public List<CreditEvent> OnPeriodStart(EconomicDate date, DifficultyDefinition difficulty, DeterministicRandom rng)
    {
        var events = new List<CreditEvent>();
        int worstPerforming = Rules.IndexOf(Rules.DefaultRating) - 1; // CCC
        foreach ((string issuer, IssuerState s) in _issuers.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            double migration = rng.NextDouble();
            double defaultRoll = rng.NextDouble();
            if (s.Defaulted || s.Player)
            {
                continue;
            }

            if (defaultRoll < SeasonalDefaultProbability(issuer, difficulty))
            {
                s.Defaulted = true;
                s.DefaultedIn = date.FiscalPeriod;
                s.Rating = Rules.DefaultRating;
                events.Add(new CreditEvent(CreditEventKind.Default, issuer, s.Rating));
                continue;
            }

            int index = Rules.IndexOf(s.Rating);
            if (migration < Rules.UpgradeProbability && index > 0)
            {
                s.Rating = Rules.Scale[index - 1].Id;
                events.Add(new CreditEvent(CreditEventKind.Upgrade, issuer, s.Rating));
            }
            else if (migration > 1 - Rules.DowngradeProbability && index < worstPerforming)
            {
                s.Rating = Rules.Scale[index + 1].Id;
                events.Add(new CreditEvent(CreditEventKind.Downgrade, issuer, s.Rating));
            }
        }

        return events;
    }

    /// <summary>Registra una sociedad del jugador como emisor (sin defaults ni migraciones aleatorias).</summary>
    public void RegisterPlayerIssuer(string issuer, string rating)
    {
        if (!_issuers.ContainsKey(issuer))
        {
            _issuers[issuer] = new IssuerState { Rating = rating, Player = true };
        }
    }

    /// <summary>Fuerza un default (comando de debug).</summary>
    public CreditEvent? ForceDefault(string issuer, EconomicDate date)
    {
        if (!_issuers.TryGetValue(issuer, out IssuerState? s) || s.Defaulted)
        {
            return null;
        }

        s.Defaulted = true;
        s.DefaultedIn = date.FiscalPeriod;
        s.Rating = Rules.DefaultRating;
        return new CreditEvent(CreditEventKind.Default, issuer, s.Rating);
    }

    /// <summary>Después del canje el emisor vuelve a pagar, con una calificación baja.</summary>
    public CreditEvent Restructure(string issuer)
    {
        IssuerState s = _issuers[issuer];
        s.Defaulted = false;
        s.DefaultedIn = null;
        s.Rating = issuer == Sovereign ? Rules.SovereignAfterRestructuring : Rules.CorporateAfterRestructuring;
        return new CreditEvent(CreditEventKind.Restructuring, issuer, s.Rating);
    }
}
