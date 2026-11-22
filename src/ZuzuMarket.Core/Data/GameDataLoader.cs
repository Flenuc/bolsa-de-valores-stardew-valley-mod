using System.Text.Json;
using System.Text.Json.Serialization;
using ZuzuMarket.Core.Ledger;
using ZuzuMarket.Core.Markets;
using ZuzuMarket.Core.Taxation;

namespace ZuzuMarket.Core.Data;

public sealed class DataLoadResult
{
    public DataLoadResult(GameData? data, IReadOnlyList<string> errors)
    {
        Data = data;
        Errors = errors;
    }

    /// <summary>Datos cargados, o <c>null</c> si hubo errores.</summary>
    public GameData? Data { get; }

    public IReadOnlyList<string> Errors { get; }

    public bool Success => Data is not null && Errors.Count == 0;
}

/// <summary>Carga y valida los archivos de <c>data/</c>.</summary>
public static class GameDataLoader
{
    public const string AccountsFile = "accounts.json";
    public const string CommoditiesFile = "commodities.json";
    public const string DifficultiesFile = "difficulties.json";
    public const string EventsFile = "events.json";
    public const string TaxesFile = "taxes.json";
    public const string CompaniesFile = "companies.json";
    public const string MarketsFile = "markets.json";
    public const string RatingsFile = "ratings.json";
    public const string BondsFile = "bonds.json";

    public static IReadOnlyList<string> RequiredDifficulties { get; } = new[] { "casual", "realista", "argentina" };

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static DataLoadResult LoadFromDirectory(string directory)
    {
        var errors = new List<string>();
        string? Read(string file)
        {
            string path = Path.Combine(directory, file);
            if (!File.Exists(path))
            {
                errors.Add($"Falta el archivo de datos {file} (buscado en {path}).");
                return null;
            }

            return File.ReadAllText(path);
        }

        string? accounts = Read(AccountsFile);
        string? commodities = Read(CommoditiesFile);
        string? difficulties = Read(DifficultiesFile);
        string? events = Read(EventsFile);
        string? taxes = Read(TaxesFile);
        string? companies = Read(CompaniesFile);
        string? markets = Read(MarketsFile);
        string? ratings = Read(RatingsFile);
        string? bonds = Read(BondsFile);
        if (accounts is null || commodities is null || difficulties is null || events is null || taxes is null || companies is null || markets is null || ratings is null || bonds is null)
        {
            return new DataLoadResult(null, errors);
        }

        return LoadFromJson(accounts, commodities, difficulties, events, taxes, companies, markets, ratings, bonds);
    }

    public static DataLoadResult LoadFromJson(string accountsJson, string commoditiesJson, string difficultiesJson, string eventsJson = "[]", string? taxesJson = null, string? companiesJson = null, string? marketsJson = null, string? ratingsJson = null, string? bondsJson = null)
    {
        var errors = new List<string>();
        List<AccountDefinition>? accounts = Deserialize<List<AccountDefinition>>(accountsJson, AccountsFile, errors);
        List<CommodityDefinition>? commodities = Deserialize<List<CommodityDefinition>>(commoditiesJson, CommoditiesFile, errors);
        List<DifficultyDefinition>? difficulties = Deserialize<List<DifficultyDefinition>>(difficultiesJson, DifficultiesFile, errors);
        List<EventDefinition>? events = Deserialize<List<EventDefinition>>(eventsJson, EventsFile, errors);
        TaxRules? taxes = taxesJson is null ? new TaxRules() : Deserialize<TaxRules>(taxesJson, TaxesFile, errors);
        List<CompanyDefinition>? companies = companiesJson is null ? new List<CompanyDefinition>() : Deserialize<List<CompanyDefinition>>(companiesJson, CompaniesFile, errors);
        MarketRules? markets = marketsJson is null ? new MarketRules() : Deserialize<MarketRules>(marketsJson, MarketsFile, errors);
        RatingRules? ratings = ratingsJson is null ? new RatingRules() : Deserialize<RatingRules>(ratingsJson, RatingsFile, errors);
        List<BondTemplate>? bonds = bondsJson is null ? new List<BondTemplate>() : Deserialize<List<BondTemplate>>(bondsJson, BondsFile, errors);
        if (accounts is null || commodities is null || difficulties is null || events is null || taxes is null || companies is null || markets is null || ratings is null || bonds is null)
        {
            return new DataLoadResult(null, errors);
        }

        ValidateAccounts(accounts, errors);
        ValidateCommodities(commodities, errors);
        ValidateDifficulties(difficulties, errors);
        ValidateEvents(events, commodities, errors);
        if (taxesJson is not null)
        {
            ValidateTaxes(taxes, errors);
        }

        ValidateCompanies(companies, commodities, errors);
        if (marketsJson is not null)
        {
            ValidateMarkets(markets, errors);
        }

        if (ratingsJson is not null)
        {
            ValidateRatings(ratings, companies, difficulties, errors);
        }

        ValidateBonds(bonds, companies, errors);

        if (errors.Count > 0)
        {
            return new DataLoadResult(null, errors);
        }

        var chart = new ChartOfAccounts(accounts.Select(a => new Account(a.Code, a.Name, a.Type)));
        return new DataLoadResult(new GameData(chart, commodities, difficulties, events, taxes, companies, markets, ratings, bonds), errors);
    }

    private static T? Deserialize<T>(string json, string file, List<string> errors)
        where T : class
    {
        try
        {
            T? result = JsonSerializer.Deserialize<T>(json, JsonOptions);
            if (result is null)
            {
                errors.Add($"{file}: el archivo está vacío.");
            }

            return result;
        }
        catch (JsonException ex)
        {
            errors.Add($"{file}: JSON inválido ({ex.Message}).");
            return null;
        }
    }

    private static void ValidateAccounts(List<AccountDefinition> accounts, List<string> errors)
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (AccountDefinition a in accounts)
        {
            if (string.IsNullOrWhiteSpace(a.Code) || string.IsNullOrWhiteSpace(a.Name))
            {
                errors.Add($"{AccountsFile}: hay una cuenta sin código o sin nombre.");
                continue;
            }

            if (!Enum.IsDefined(typeof(AccountType), a.Type))
            {
                errors.Add($"{AccountsFile}: la cuenta {a.Code} tiene un tipo inválido.");
            }

            if (!codes.Add(a.Code))
            {
                errors.Add($"{AccountsFile}: código de cuenta duplicado {a.Code}.");
            }
        }

        foreach (string required in SystemAccounts.All)
        {
            if (!codes.Contains(required))
            {
                errors.Add($"{AccountsFile}: falta la cuenta de sistema {required}.");
            }
        }
    }

    private static void ValidateCommodities(List<CommodityDefinition> commodities, List<string> errors)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var itemOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (CommodityDefinition c in commodities)
        {
            if (string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Name))
            {
                errors.Add($"{CommoditiesFile}: hay un commodity sin id o sin nombre.");
                continue;
            }

            if (!ids.Add(c.Id))
            {
                errors.Add($"{CommoditiesFile}: id de commodity duplicado {c.Id}.");
            }

            if (string.IsNullOrWhiteSpace(c.Sector))
            {
                errors.Add($"{CommoditiesFile}: {c.Id} no tiene sector.");
            }

            if (c.ItemIds.Count == 0)
            {
                errors.Add($"{CommoditiesFile}: {c.Id} no tiene objetos del juego asociados.");
            }

            foreach (string itemId in c.ItemIds)
            {
                if (itemOwners.TryGetValue(itemId, out string? owner))
                {
                    errors.Add($"{CommoditiesFile}: el objeto {itemId} está en {owner} y en {c.Id}.");
                }
                else
                {
                    itemOwners[itemId] = c.Id;
                }
            }

            if (c.AllYear == (c.HarvestSeasons.Count > 0))
            {
                errors.Add($"{CommoditiesFile}: {c.Id} debe tener estaciones de cosecha o allYear=true (no ambos).");
            }

            if (c.FuturesContractSize <= 0)
            {
                errors.Add($"{CommoditiesFile}: {c.Id} necesita futuresContractSize > 0.");
            }

            ValidateMarket(c, errors);
        }
    }

    private static void ValidateMarket(CommodityDefinition c, List<string> errors)
    {
        CommodityMarketParameters? m = c.Market;
        if (m is null)
        {
            errors.Add($"{CommoditiesFile}: {c.Id} tiene el bloque market vacío.");
            return;
        }

        if (m.Volatility < 0 || m.Volatility > 0.5)
        {
            errors.Add($"{CommoditiesFile}: {c.Id} tiene volatility fuera de [0, 0.5].");
        }

        if (m.MeanReversion <= 0 || m.MeanReversion >= 1)
        {
            errors.Add($"{CommoditiesFile}: {c.Id} tiene meanReversion fuera de (0, 1).");
        }

        if (m.StockDecay <= 0 || m.StockDecay >= 1)
        {
            errors.Add($"{CommoditiesFile}: {c.Id} tiene stockDecay fuera de (0, 1).");
        }

        if (m.BaseVolume <= 0 || m.Impact < 0 || m.InSeasonFactor <= 0 || m.OffSeasonFactor <= 0)
        {
            errors.Add($"{CommoditiesFile}: {c.Id} necesita baseVolume, inSeasonFactor y offSeasonFactor > 0 e impact >= 0.");
        }
    }

    private static void ValidateEvents(List<EventDefinition> events, List<CommodityDefinition> commodities, List<string> errors)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var commodityIds = new HashSet<string>(commodities.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
        var sectors = new HashSet<string>(commodities.Select(c => c.Sector), StringComparer.OrdinalIgnoreCase);
        foreach (EventDefinition e in events)
        {
            if (string.IsNullOrWhiteSpace(e.Id))
            {
                errors.Add($"{EventsFile}: hay un evento sin id.");
                continue;
            }

            if (!ids.Add(e.Id))
            {
                errors.Add($"{EventsFile}: id de evento duplicado {e.Id}.");
            }

            if (e.Probability is < 0 or > 1)
            {
                errors.Add($"{EventsFile}: {e.Id} tiene una probabilidad fuera de [0, 1].");
            }

            if (e.DurationDays < 1 || e.CooldownDays < 0 || e.MinYear < 1)
            {
                errors.Add($"{EventsFile}: {e.Id} necesita durationDays >= 1, cooldownDays >= 0 y minYear >= 1.");
            }

            if (e.FixedDate is { } fixedDate && (fixedDate.Day < 1 || fixedDate.Day > Time.EconomicDate.DaysPerSeason))
            {
                errors.Add($"{EventsFile}: {e.Id} tiene una fecha fija inválida.");
            }

            if (e.Effects.Count == 0 && e.InflationShock == 0)
            {
                errors.Add($"{EventsFile}: {e.Id} no tiene efectos.");
            }

            foreach (EventEffect effect in e.Effects)
            {
                string target = effect.Target ?? "";
                bool valid = string.Equals(target, "all", StringComparison.OrdinalIgnoreCase)
                    || (target.StartsWith(EventDefinition.SectorPrefix, StringComparison.OrdinalIgnoreCase)
                        && sectors.Contains(target.Substring(EventDefinition.SectorPrefix.Length)))
                    || commodityIds.Contains(target);
                if (!valid)
                {
                    errors.Add($"{EventsFile}: {e.Id} apunta a un destino inexistente: \"{target}\".");
                }

                if (Math.Abs(effect.PriceImpact) > 1.5)
                {
                    errors.Add($"{EventsFile}: {e.Id} tiene un priceImpact desmedido ({effect.PriceImpact}).");
                }
            }
        }
    }

    private static void ValidateCompanies(List<CompanyDefinition> companies, List<CommodityDefinition> commodities, List<string> errors)
    {
        var tickers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var commodityIds = new HashSet<string>(commodities.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
        var sectors = new HashSet<string>(commodities.Select(c => c.Sector), StringComparer.OrdinalIgnoreCase);
        foreach (CompanyDefinition c in companies)
        {
            if (string.IsNullOrWhiteSpace(c.Ticker) || string.IsNullOrWhiteSpace(c.Name))
            {
                errors.Add($"{CompaniesFile}: hay una empresa sin ticker o sin nombre.");
                continue;
            }

            if (!tickers.Add(c.Ticker))
            {
                errors.Add($"{CompaniesFile}: ticker duplicado {c.Ticker}.");
            }

            if (c.Shares <= 0 || c.BaseEps <= 0 || c.Pe <= 0 || c.AverageVolume <= 0)
            {
                errors.Add($"{CompaniesFile}: {c.Ticker} necesita shares, baseEps, pe y averageVolume > 0.");
            }

            if (c.Payout is < 0 or > 1 || c.Volatility is < 0 or > 0.2 || c.MeanReversion is <= 0 or >= 1)
            {
                errors.Add($"{CompaniesFile}: {c.Ticker} tiene payout, volatility o meanReversion fuera de rango.");
            }

            foreach (Exposure e in c.Exposures)
            {
                string target = e.Target ?? "";
                bool valid = string.Equals(target, "all", StringComparison.OrdinalIgnoreCase)
                    || (target.StartsWith(EventDefinition.SectorPrefix, StringComparison.OrdinalIgnoreCase)
                        && sectors.Contains(target.Substring(EventDefinition.SectorPrefix.Length)))
                    || commodityIds.Contains(target);
                if (!valid)
                {
                    errors.Add($"{CompaniesFile}: {c.Ticker} está expuesta a un destino inexistente: \"{target}\".");
                }
            }
        }
    }

    private static void ValidateRatings(RatingRules r, List<CompanyDefinition> companies, List<DifficultyDefinition> difficulties, List<string> errors)
    {
        if (r.Scale.Count < 3)
        {
            errors.Add($"{RatingsFile}: la escala necesita al menos 3 calificaciones (la última es el default).");
            return;
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RatingLevel level in r.Scale)
        {
            if (string.IsNullOrWhiteSpace(level.Id) || !ids.Add(level.Id))
            {
                errors.Add($"{RatingsFile}: calificación vacía o duplicada ({level.Id}).");
            }

            if (level.Spread is < 0 or > 0.5 || level.AnnualDefault is < 0 or > 1)
            {
                errors.Add($"{RatingsFile}: {level.Id} tiene spread o annualDefault fuera de rango.");
            }
        }

        IEnumerable<string> referenced = r.SovereignByDifficulty.Values.Concat(r.Companies.Values)
            .Append(r.SovereignAfterRestructuring).Append(r.CorporateAfterRestructuring);
        foreach (string rating in referenced.Where(x => !ids.Contains(x)).Distinct())
        {
            errors.Add($"{RatingsFile}: la calificación {rating} no está en la escala.");
        }

        foreach (string ticker in r.Companies.Keys.Where(t => companies.All(c => !string.Equals(c.Ticker, t, StringComparison.OrdinalIgnoreCase))))
        {
            errors.Add($"{RatingsFile}: la empresa {ticker} no existe.");
        }

        if (r.SovereignRecovery is < 0 or > 1 || r.CorporateRecovery is < 0 or > 1 || r.RestructuringDelay < 1)
        {
            errors.Add($"{RatingsFile}: recupero fuera de [0, 1] o restructuringDelay < 1.");
        }
    }

    private static void ValidateBonds(List<BondTemplate> bonds, List<CompanyDefinition> companies, List<string> errors)
    {
        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BondTemplate b in bonds)
        {
            if (string.IsNullOrWhiteSpace(b.Prefix) || !prefixes.Add(b.Prefix))
            {
                errors.Add($"{BondsFile}: prefijo vacío o duplicado ({b.Prefix}).");
            }

            bool issuerOk = b.Issuer == CreditEngine.Sovereign || companies.Any(c => c.Ticker == b.Issuer);
            if (!issuerOk)
            {
                errors.Add($"{BondsFile}: {b.Prefix} tiene un emisor inexistente: {b.Issuer}.");
            }

            if (b.TenorSeasons < 1 || b.AmortizationInstallments < 1 || b.AmortizationInstallments > b.TenorSeasons || b.Coupon is < -0.05 or > 0.5)
            {
                errors.Add($"{BondsFile}: {b.Prefix} tiene plazo, amortización o cupón inválidos.");
            }
        }
    }

    private static void ValidateMarkets(MarketRules m, List<string> errors)
    {
        if (m.CommissionRate is < 0 or > 0.1m || m.MarketFeeRate is < 0 or > 0.1m || m.CommissionIvaRate is < 0 or > 1 || m.DividendWithholding is < 0 or > 1)
        {
            errors.Add($"{MarketsFile}: comisiones, derechos, IVA o retención fuera de rango.");
        }

        if (m.StockSpread is < 0 or > 0.2 || m.LetraSpread is < 0 or > 0.2 || m.MaxImpact is < 0 or > 0.5)
        {
            errors.Add($"{MarketsFile}: spreads o impacto fuera de rango.");
        }

        if (m.LetraTenors.Count == 0 || m.LetraTenors.Any(t => t.Days < 1) || m.LetraFaceValue <= 0)
        {
            errors.Add($"{MarketsFile}: se necesita al menos un plazo de Letras (days >= 1) y letraFaceValue > 0.");
        }

        if (m.CaucionTerms.Count == 0 || m.CaucionTerms.Any(d => d < 1) || m.CaucionMinimum < 0)
        {
            errors.Add($"{MarketsFile}: se necesita al menos un plazo de caución (>= 1 día).");
        }

        if (m.IntradayTicks < 1 || m.MerzuBase <= 0)
        {
            errors.Add($"{MarketsFile}: intradayTicks y merzuBase deben ser positivos.");
        }
    }

    private static void ValidateTaxes(TaxRules t, List<string> errors)
    {
        void Rate(string name, decimal rate)
        {
            if (rate is < 0 or > 1)
            {
                errors.Add($"{TaxesFile}: la alícuota {name} está fuera de [0, 1].");
            }
        }

        if (t.DueDay is < 1 or > Time.EconomicDate.DaysPerSeason || t.GananciasDueDay is < 1 or > Time.EconomicDate.DaysPerSeason)
        {
            errors.Add($"{TaxesFile}: dueDay y gananciasDueDay deben estar entre 1 y 28.");
        }

        Rate("iva.default", t.Iva.Default);
        Rate("iva.purchaseRate", t.Iva.PurchaseRate);
        foreach (var kv in t.Iva.Rates)
        {
            Rate($"iva.{kv.Key}", kv.Value);
        }

        Rate("ingresosBrutos.default", t.IngresosBrutos.Default);
        foreach (var kv in t.IngresosBrutos.Rates)
        {
            Rate($"ingresosBrutos.{kv.Key}", kv.Value);
        }

        Rate("seguridadHigiene.rate", t.SeguridadHigiene.Rate);
        Rate("mora.dailyInterest", t.Mora.DailyInterest);
        Rate("mora.fineRate", t.Mora.FineRate);

        List<MonotributoCategory> categories = t.Monotributo.Categories;
        if (categories.Count == 0)
        {
            errors.Add($"{TaxesFile}: el Monotributo necesita al menos una categoría.");
        }

        for (int i = 0; i < categories.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(categories[i].Id) || categories[i].AnnualCap <= 0 || categories[i].Fee < 0)
            {
                errors.Add($"{TaxesFile}: la categoría {i + 1} del Monotributo necesita id, annualCap > 0 y fee >= 0.");
            }

            if (i > 0 && categories[i].AnnualCap <= categories[i - 1].AnnualCap)
            {
                errors.Add($"{TaxesFile}: los topes del Monotributo deben ser crecientes ({categories[i].Id}).");
            }
        }

        List<TaxBracket> brackets = t.Ganancias.Brackets;
        if (brackets.Count == 0 || brackets[^1].UpTo is not null)
        {
            errors.Add($"{TaxesFile}: la escala de Ganancias debe terminar con un tramo sin límite (upTo: null).");
        }

        for (int i = 0; i < brackets.Count; i++)
        {
            Rate($"ganancias.brackets[{i}]", brackets[i].Rate);
            if (i > 0 && brackets[i].UpTo is decimal upper && brackets[i - 1].UpTo is decimal lower && upper <= lower)
            {
                errors.Add($"{TaxesFile}: los tramos de Ganancias deben ser crecientes.");
            }
        }

        if (t.Ganancias.Advances < 0 || t.Ganancias.Advances > 3)
        {
            errors.Add($"{TaxesFile}: ganancias.advances debe estar entre 0 y 3 (verano, otoño e invierno).");
        }
    }

    private static void ValidateDifficulties(List<DifficultyDefinition> difficulties, List<string> errors)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DifficultyDefinition d in difficulties)
        {
            if (string.IsNullOrWhiteSpace(d.Id))
            {
                errors.Add($"{DifficultiesFile}: hay una dificultad sin id.");
                continue;
            }

            if (!ids.Add(d.Id))
            {
                errors.Add($"{DifficultiesFile}: id de dificultad duplicado {d.Id}.");
            }

            if (d.InflationMin < 0 || d.InflationMax < d.InflationMin)
            {
                errors.Add($"{DifficultiesFile}: {d.Id} tiene un rango de inflación inválido.");
            }

            if (d.SovereignDefaultProbability is < 0 or > 1)
            {
                errors.Add($"{DifficultiesFile}: {d.Id} tiene una probabilidad de default fuera de [0, 1].");
            }
        }

        foreach (string required in RequiredDifficulties)
        {
            if (!ids.Contains(required))
            {
                errors.Add($"{DifficultiesFile}: falta la dificultad {required}.");
            }
        }
    }
}
