using System.Globalization;
using ZuzuMarket.Core.Ledger;
using ZuzuMarket.Core.Markets;
using ZuzuMarket.Core.Money;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Companies;

/// <summary>Mercado de capitales de las S.A. del jugador: ON propias, IPO, recompra y colocación (Fase 7B).</summary>
public sealed partial class PlayerCompanies
{
    public CapitalMarketRules Capital => Rules.CapitalMarket;

    public bool IsSA(CompanyState c) => Rules.FindForm(c.Form)?.CanListOnMarket == true;

    // ───────────────────────────── Ticker y valuación ─────────────────────────────

    /// <summary>Las S.A. reciben un ticker y se registran como emisor (sin defaults aleatorios).</summary>
    private void AssignTicker(CompanyState c)
    {
        if (!IsSA(c))
        {
            return;
        }

        if (c.Ticker is null)
        {
            string baseTicker = Rules.FindType(c.Type)?.Ticker is { Length: > 0 } t ? t : c.Type.ToUpperInvariant()[..4];
            string ticker = baseTicker;
            int n = 2;
            while (_stocks.IsFictional(ticker) || _s.Companies.Any(o => o != c && o.Ticker == ticker))
            {
                ticker = baseTicker + n.ToString(CultureInfo.InvariantCulture);
                n++;
            }

            c.Ticker = ticker;
        }

        _credit.RegisterPlayerIssuer(c.Ticker, c.Rating);
    }

    /// <summary>Resultado acumulado de toda la vida de la sociedad (ingresos − egresos).</summary>
    public decimal CumulativeProfit(CompanyState c)
    {
        decimal profit = 0;
        foreach (TrialBalanceRow row in _ledger.GetTrialBalance(c.Id))
        {
            if (row.Account.Type == AccountType.Ingreso)
            {
                profit += row.Balance;
            }
            else if (row.Account.Type == AccountType.Egreso)
            {
                profit -= row.Balance;
            }
        }

        return profit;
    }

    /// <summary>Resultado anualizado de las últimas (hasta) 4 estaciones cerradas.</summary>
    public decimal TrailingAnnualProfit(CompanyState c)
    {
        List<decimal> snaps = c.ProfitSnapshots;
        if (snaps.Count < 2)
        {
            // Una sola estación cerrada (o ninguna): lo de la primera, anualizado.
            return snaps.Count == 1 ? snaps[0] * EconomicDate.SeasonsPerYear : 0;
        }

        int seasons = Math.Min(EconomicDate.SeasonsPerYear, snaps.Count - 1);
        decimal profit = snaps[^1] - snaps[^(seasons + 1)];
        return profit * EconomicDate.SeasonsPerYear / seasons;
    }

    private long SharesFor(CompanyState c) => c.Listing?.TotalShares ?? Capital.IpoShares;

    /// <summary>UPA anual (con piso) que usa el mercado.</summary>
    public double MarketEps(CompanyState c) =>
        Math.Max(Capital.EpsFloor * _inflation.Index, (double)TrailingAnnualProfit(c) / SharesFor(c));

    public double Pe(CompanyState c) => Rules.FindType(c.Type)?.Pe ?? 10;

    /// <summary>Valor fundamental por acción: P/E del rubro × UPA.</summary>
    public decimal FundamentalPrice(CompanyState c) => MoneyRounding.Round((decimal)(Pe(c) * MarketEps(c)));

    /// <summary>Precio de colocación en la IPO (con descuento).</summary>
    public decimal IpoPrice(CompanyState c) => MoneyRounding.Round(FundamentalPrice(c) * (1 - Capital.IpoDiscount));

    public decimal MarketPrice(CompanyState c) =>
        c.Listing is not null && c.Ticker is { } t ? MoneyRounding.Round((decimal)_stocks.PriceAt(t, MarketHours.Close)) : FundamentalPrice(c);

    private CompanyDefinition Definition(CompanyState c) => new()
    {
        Ticker = c.Ticker!,
        Name = $"{CultureInfo.InvariantCulture.TextInfo.ToTitleCase(c.Type)} S.A.",
        Sector = c.Type,
        Shares = c.Listing?.TotalShares ?? Capital.IpoShares,
        BaseEps = MarketEps(c),
        Pe = Pe(c),
        Payout = 0,
        Volatility = Capital.Volatility,
        MeanReversion = Capital.MeanReversion,
        AverageVolume = Math.Max(100, (c.Listing?.PublicShares ?? 0) * 0.01),
    };

    private bool RatingOk(CompanyState c) =>
        _data.Ratings.IndexOf(c.Rating) >= 0 && _data.Ratings.IndexOf(c.Rating) <= _data.Ratings.IndexOf(Capital.MinRating);

    /// <summary>Debug: le suma un resultado a la sociedad y cierra una "estación" para la UPA.</summary>
    public bool DebugProfit(string id, decimal amount, EconomicDate date)
    {
        if (Find(id) is not { Active: true } c)
        {
            return false;
        }

        if (c.ProfitSnapshots.Count == 0)
        {
            c.ProfitSnapshots.Add(CumulativeProfit(c));
        }

        PostCompany(c, date, "Resultado de prueba", "entry.test-debt",
            JournalLine.ToDebit(SystemAccounts.Caja, amount),
            JournalLine.ToCredit(SystemAccounts.IngresosNoClasificados, amount));
        c.ProfitSnapshots.Add(CumulativeProfit(c));
        return true;
    }

    // ───────────────────────────── IPO ─────────────────────────────

    public CompanyError CanIpo(CompanyState c, long primary, long secondary)
    {
        if (!c.Active)
        {
            return CompanyError.NotFound;
        }

        if (!IsSA(c))
        {
            return CompanyError.NotSA;
        }

        if (c.Listing is not null)
        {
            return CompanyError.AlreadyListed;
        }

        if (_legal.IsInhibited)
        {
            return CompanyError.Inhibited;
        }

        if (!RatingOk(c))
        {
            return CompanyError.LowRating;
        }

        if (c.ProfitSnapshots.Count == 0 || TrailingAnnualProfit(c) <= 0)
        {
            return CompanyError.NoHistory;
        }

        long total = Capital.IpoShares + primary;
        decimal floatShare = (decimal)(primary + secondary) / total;
        return primary < 0 || secondary < 0 || secondary > Capital.IpoShares || floatShare < Capital.MinFloat || floatShare > Capital.MaxFloat
            ? CompanyError.InvalidFloat
            : CompanyError.None;
    }

    /// <summary>
    /// Salida a bolsa: <paramref name="primary"/> acciones nuevas (el oro entra a la sociedad) y
    /// <paramref name="secondary"/> acciones tuyas (el oro va a tu bolsillo), al precio de colocación menos la comisión.
    /// </summary>
    public CompanyResult Ipo(string id, long primary, long secondary, EconomicDate date, List<CompanyNotice> notices)
    {
        if (Find(id) is not { } c)
        {
            return new CompanyResult(CompanyError.NotFound);
        }

        CompanyError error = CanIpo(c, primary, secondary);
        if (error != CompanyError.None)
        {
            return new CompanyResult(error);
        }

        decimal price = IpoPrice(c);
        double eps = MarketEps(c);
        if (primary > 0)
        {
            decimal gross = MoneyRounding.Round(price * primary);
            decimal fee = MoneyRounding.Round(gross * Capital.UnderwritingFee);
            PostCompany(c, date, "Oferta pública inicial: acciones nuevas", "entry.company-ipo-primary",
                JournalLine.ToDebit(SystemAccounts.Caja, gross - fee),
                JournalLine.ToDebit(SystemAccounts.GastosDeColocacion, fee),
                JournalLine.ToCredit(SystemAccounts.PrimaDeEmision, gross));
            TaxesOf(c).AddDeductibleExpense(fee);
        }

        c.Listing = new ListingState { ListedOn = date, TotalShares = Capital.IpoShares + primary, PlayerShares = Capital.IpoShares, IpoPrice = price };
        decimal toPlayer = secondary > 0 ? SellPlayerShares(c, secondary, price, date, "entry.company-ipo-secondary") : 0;

        // Abre al valor fundamental (la colocación se hace con descuento).
        _stocks.List(Definition(c), (double)FundamentalPrice(c), eps, date);
        notices.Add(new CompanyNotice(CompanyNoticeKind.Listed, date, c.Id, price, c.Ticker));
        return new CompanyResult(CompanyError.None, toPlayer, c);
    }

    /// <summary>Recompra (OPA): comprás acciones al precio de mercado + prima. Al llegar al 100% deja de cotizar.</summary>
    public CompanyResult Buyback(string id, long shares, decimal pocket, EconomicDate date, List<CompanyNotice> notices)
    {
        if (Find(id) is not { Active: true, Listing: { } listing } c)
        {
            return new CompanyResult(CompanyError.NotListed);
        }

        if (shares < 1 || shares > listing.PublicShares)
        {
            return new CompanyResult(CompanyError.InvalidFloat);
        }

        decimal cost = MoneyRounding.Round(MarketPrice(c) * (1 + Capital.BuybackPremium) * shares);
        if (cost > pocket)
        {
            return new CompanyResult(CompanyError.InsufficientFunds);
        }

        PostPlayer(date, $"Recompra de acciones de {c.Ticker}", "entry.company-buyback",
            JournalLine.ToDebit(SystemAccounts.ParticipacionesEnSociedades, cost),
            JournalLine.ToCredit(SystemAccounts.Caja, cost));
        c.Contributed += cost;
        listing.PlayerShares += shares;
        notices.Add(new CompanyNotice(CompanyNoticeKind.Buyback, date, c.Id, cost, shares.ToString(CultureInfo.InvariantCulture)));
        if (listing.PublicShares == 0)
        {
            _stocks.Delist(c.Ticker!);
            c.Listing = null;
            notices.Add(new CompanyNotice(CompanyNoticeKind.Delisted, date, c.Id, 0, c.Ticker));
        }

        return new CompanyResult(CompanyError.None, -cost, c);
    }

    /// <summary>Colocación: vendés acciones tuyas al precio de mercado − descuento, conservando el control.</summary>
    public CompanyResult FollowOn(string id, long shares, EconomicDate date, List<CompanyNotice> notices)
    {
        if (Find(id) is not { Active: true, Listing: { } listing } c)
        {
            return new CompanyResult(CompanyError.NotListed);
        }

        if (shares < 1 || (decimal)(listing.PublicShares + shares) / listing.TotalShares > Capital.MaxFloat)
        {
            return new CompanyResult(CompanyError.InvalidFloat);
        }

        decimal price = MoneyRounding.Round(MarketPrice(c) * (1 - Capital.FollowOnDiscount));
        decimal net = SellPlayerShares(c, shares, price, date, "entry.company-follow-on");
        notices.Add(new CompanyNotice(CompanyNoticeKind.FollowOn, date, c.Id, net, shares.ToString(CultureInfo.InvariantCulture)));
        return new CompanyResult(CompanyError.None, net, c);
    }

    /// <summary>El jugador vende acciones suyas: cobra neto de comisión y baja su participación a costo.</summary>
    private decimal SellPlayerShares(CompanyState c, long shares, decimal price, EconomicDate date, string key)
    {
        ListingState listing = c.Listing!;
        decimal gross = MoneyRounding.Round(price * shares);
        decimal fee = MoneyRounding.Round(gross * Capital.UnderwritingFee);
        decimal cost = MoneyRounding.Round(c.Contributed * shares / listing.PlayerShares);
        decimal result = gross - fee - cost;
        var lines = new List<JournalLine>
        {
            JournalLine.ToDebit(SystemAccounts.Caja, gross - fee),
            JournalLine.ToDebit(SystemAccounts.ComisionesBursatiles, fee),
        };
        if (cost > 0)
        {
            lines.Add(JournalLine.ToCredit(SystemAccounts.ParticipacionesEnSociedades, cost));
        }

        decimal gain = result + fee; // resultado antes de comisión
        if (gain > 0)
        {
            lines.Add(JournalLine.ToCredit(SystemAccounts.ResultadosVentaPositivos, gain));
        }
        else if (gain < 0)
        {
            lines.Add(JournalLine.ToDebit(SystemAccounts.ResultadosVentaNegativos, -gain));
        }

        PostPlayer(date, $"Venta de acciones de {c.Ticker}", key, lines.ToArray());
        c.Contributed -= cost;
        listing.PlayerShares -= shares;
        return gross - fee;
    }

    // ───────────────────────────── ON propias ─────────────────────────────

    public decimal BondsOutstanding(CompanyState c) => _ledger.GetBalance(c.Id, SystemAccounts.OnEmitidas);

    /// <summary>Máximo a emitir: (facturación anual + patrimonio) × factor de la calificación − ON vigentes.</summary>
    public decimal BondLimit(CompanyState c) =>
        Math.Max(0, Math.Floor((((AnnualRevenue(c) + Math.Max(0, Equity(c))) * _data.Legal.Factor(c.Rating)) - BondsOutstanding(c)) / (decimal)BondMath.Face) * (decimal)BondMath.Face);

    /// <summary>Cupón fijo por estación: curva + spread de la calificación de la sociedad.</summary>
    public double BondCoupon(CompanyState c, int term) =>
        c.Ticker is null ? 0 : Math.Round(_broker.Yield(term * EconomicDate.DaysPerSeason, c.Ticker) / 0.0025) * 0.0025;

    public CompanyError CanIssueBond(CompanyState c, decimal amount, int term)
    {
        if (!c.Active)
        {
            return CompanyError.NotFound;
        }

        if (!IsSA(c) || c.Ticker is null)
        {
            return CompanyError.NotSA;
        }

        if (_legal.IsInhibited)
        {
            return CompanyError.Inhibited;
        }

        if (!RatingOk(c))
        {
            return CompanyError.LowRating;
        }

        if (!Capital.BondTerms.Contains(term))
        {
            return CompanyError.InvalidTerm;
        }

        if (amount < Capital.BondMinimum)
        {
            return CompanyError.BelowMinimum;
        }

        return amount > BondLimit(c) ? CompanyError.AboveLimit : CompanyError.None;
    }

    /// <summary>Emite una ON: la serie cotiza en BYMZ y el oro (menos la colocación) entra a la sociedad.</summary>
    public CompanyResult IssueBond(string id, decimal amount, int term, int installments, EconomicDate date, List<CompanyNotice> notices)
    {
        if (Find(id) is not { } c)
        {
            return new CompanyResult(CompanyError.NotFound);
        }

        decimal face = (decimal)BondMath.Face;
        amount = Math.Floor(amount / face) * face;
        CompanyError error = CanIssueBond(c, amount, term);
        if (error != CompanyError.None)
        {
            return new CompanyResult(error);
        }

        c.BondsIssued++;
        var series = new BondSeries
        {
            Id = $"ON{c.Ticker}{c.BondsIssued}",
            Prefix = $"ON{c.Ticker}",
            Issuer = c.Ticker!,
            Type = BondType.Fixed,
            IssueDate = date.FiscalPeriod.FirstDay,
            TenorSeasons = term,
            CouponRate = BondCoupon(c, term),
            AmortizationInstallments = Math.Clamp(installments, 1, term),
            CerBase = _inflation.Index,
            IssuedUnits = (long)(amount / (decimal)BondMath.Face),
        };
        _broker.AddSeries(series);

        decimal fee = MoneyRounding.Round(amount * Capital.BondFee);
        PostCompany(c, date, $"Emisión de {series.Id}", "entry.company-bond-issued",
            JournalLine.ToDebit(SystemAccounts.Caja, amount - fee),
            JournalLine.ToDebit(SystemAccounts.GastosDeColocacion, fee),
            JournalLine.ToCredit(SystemAccounts.OnEmitidas, amount));
        TaxesOf(c).AddDeductibleExpense(fee);
        notices.Add(new CompanyNotice(CompanyNoticeKind.BondsIssued, date, c.Id, amount, series.Id));
        return new CompanyResult(CompanyError.None, 0, c);
    }

    /// <summary>Pago de un servicio de ON con la caja de la sociedad; si no alcanza, la sociedad quiebra.</summary>
    private bool PayBondService(BondSeries bond, BondCashFlow flow, EconomicDate date)
    {
        if (Active.FirstOrDefault(c => c.Ticker == bond.Issuer) is not { } c)
        {
            return true;
        }

        decimal coupon = MoneyRounding.Round((decimal)flow.Coupon * bond.IssuedUnits);
        decimal amortization = MoneyRounding.Round((decimal)flow.Amortization * bond.IssuedUnits);
        if (Cash(c) < coupon + amortization)
        {
            Bankrupt(c, date, _pending);
            return false;
        }

        var lines = new List<JournalLine>();
        if (coupon > 0)
        {
            lines.Add(JournalLine.ToDebit(SystemAccounts.InteresesDeOn, coupon));
            TaxesOf(c).AddDeductibleExpense(coupon);
        }

        if (amortization > 0)
        {
            lines.Add(JournalLine.ToDebit(SystemAccounts.OnEmitidas, Math.Min(amortization, BondsOutstanding(c))));
            decimal extra = amortization - Math.Min(amortization, BondsOutstanding(c));
            if (extra > 0)
            {
                lines.Add(JournalLine.ToDebit(SystemAccounts.InteresesDeOn, extra));
            }
        }

        if (lines.Count == 0)
        {
            return true;
        }

        lines.Add(JournalLine.ToCredit(SystemAccounts.Caja, coupon + amortization));
        PostCompany(c, date, $"Servicio de {bond.Id}", "entry.company-bond-paid", lines.ToArray());
        _pending.Add(new CompanyNotice(CompanyNoticeKind.BondPaid, date, c.Id, coupon + amortization, bond.Id));
        return true;
    }
}
