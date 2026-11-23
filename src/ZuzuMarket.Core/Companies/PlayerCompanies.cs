using System.Globalization;
using ZuzuMarket.Core.Commodities;
using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Ledger;
using ZuzuMarket.Core.Legal;
using ZuzuMarket.Core.Macro;
using ZuzuMarket.Core.Markets;
using ZuzuMarket.Core.Money;
using ZuzuMarket.Core.Taxation;
using ZuzuMarket.Core.Time;
using ZuzuMarket.Core.Trade;

namespace ZuzuMarket.Core.Companies;

/// <summary>Resultado de una operación del socio: el oro que el juego tiene que sumar (+) o restar (−) del bolsillo.</summary>
public sealed record CompanyResult(CompanyError Error, decimal GoldDelta = 0, CompanyState? Company = null);

/// <summary>
/// Sociedades del jugador (§8.2 del diseño): alta, aportes, dividendos, transformación, disolución, quiebra
/// societaria, atribución de ventas y exportación a Gotoro. Cada sociedad lleva su propio libro e impuestos.
/// </summary>
public sealed partial class PlayerCompanies
{
    private const string Player = LedgerEntities.Jugador;

    private readonly GameData _data;
    private readonly CompaniesState _s;
    private readonly GeneralLedger _ledger;
    private readonly TaxEngine _playerTaxes;
    private readonly LegalEngine _legal;
    private readonly InflationModel _inflation;
    private readonly CommodityMarket _market;
    private readonly Func<DifficultyDefinition> _difficulty;
    private readonly Dictionary<string, TaxEngine> _taxes = new();
    private readonly Broker _broker;
    private readonly CreditEngine _credit;
    private readonly StockMarket _stocks;

    /// <summary>Avisos generados fuera del día a día (p. ej. al pagar ON desde el broker).</summary>
    private readonly List<CompanyNotice> _pending = new();

    public PlayerCompanies(GameData data, CompaniesState state, GeneralLedger ledger, TaxEngine playerTaxes, LegalEngine legal, InflationModel inflation, CommodityMarket market, Func<DifficultyDefinition> difficulty,
        Broker broker, CreditEngine credit, StockMarket stocks)
    {
        _broker = broker;
        _credit = credit;
        _stocks = stocks;
        broker.IsOwnCompany = ticker => Active.Any(c => c.Listing is not null && string.Equals(c.Ticker, ticker, StringComparison.OrdinalIgnoreCase));
        broker.PayIssuer = PayBondService;
        stocks.Listed = () => Active.Where(c => c.Listing is not null).Select(Definition).ToList();
        stocks.EpsOverride = ticker => Active.FirstOrDefault(c => c.Listing is not null && string.Equals(c.Ticker, ticker, StringComparison.OrdinalIgnoreCase)) is { } listed ? MarketEps(listed) : null;
        _data = data;
        _s = state;
        _ledger = ledger;
        _playerTaxes = playerTaxes;
        _legal = legal;
        _inflation = inflation;
        _market = market;
        _difficulty = difficulty;
        foreach (CompanyState c in _s.Companies)
        {
            Attach(c);
        }
    }

    public PlayerCompanyRules Rules => _data.PlayerCompanies;

    public TradeRules Trade => _data.Trade;

    /// <summary>Liquidación de las exportaciones (dólar blend con cepo; 1 sin cepo).</summary>
    public Func<double> ExportFactor { get; set; } = () => 1;

    public CompaniesState State => _s;

    public IEnumerable<CompanyState> Active => _s.Companies.Where(c => c.Active);

    public CompanyState? Find(string id) => _s.Companies.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public CompanyState? ActiveOfType(string type) => Active.FirstOrDefault(c => c.Type.Equals(type, StringComparison.OrdinalIgnoreCase));

    public TaxEngine TaxesOf(CompanyState c) => _taxes[c.Id];

    public CompanyState? Exporter => Active.FirstOrDefault(c => Rules.FindType(c.Type)?.Exports == true);

    private decimal Ipc => (decimal)_inflation.Index;

    public decimal MinimumCapital(string form) => MoneyRounding.ToGold((Rules.FindForm(form)?.MinimumCapital ?? 0) * Ipc);

    public decimal RegistrationFee(string form) => MoneyRounding.ToGold((Rules.FindForm(form)?.RegistrationFee ?? 0) * Ipc);

    public decimal TransformationFee => MoneyRounding.ToGold(Rules.TransformationFee * Ipc);

    public decimal Cash(CompanyState c) => _ledger.GetBalance(c.Id, SystemAccounts.Caja);

    public decimal CapitalOf(CompanyState c) => _ledger.GetBalance(c.Id, SystemAccounts.Capital);

    /// <summary>Lo que el jugador aportó a la sociedad (costo de su participación).</summary>
    public decimal Contributed(CompanyState c) => c.Contributed;

    /// <summary>Resultados no asignados: resultados acumulados + ingresos − egresos.</summary>
    public decimal RetainedEarnings(CompanyState c)
    {
        decimal result = _ledger.GetBalance(c.Id, SystemAccounts.ResultadosAcumulados);
        foreach (TrialBalanceRow row in _ledger.GetTrialBalance(c.Id))
        {
            if (row.Account.Type == AccountType.Ingreso)
            {
                result += row.Balance;
            }
            else if (row.Account.Type == AccountType.Egreso)
            {
                result -= row.Balance;
            }
        }

        return result;
    }

    public decimal Equity(CompanyState c)
    {
        decimal equity = 0;
        foreach (TrialBalanceRow row in _ledger.GetTrialBalance(c.Id))
        {
            if (row.Account.Type == AccountType.Activo)
            {
                equity += row.Balance;
            }
            else if (row.Account.Type == AccountType.Pasivo)
            {
                equity -= row.Balance;
            }
        }

        return equity;
    }

    /// <summary>
    /// Dividendos que se pueden distribuir hoy: resultados no asignados, pagados con la caja que sobra después de
    /// cubrir todas las deudas (boletas y pasivos devengados) y sin tocar el capital.
    /// </summary>
    public decimal Distributable(CompanyState c)
    {
        decimal liabilities = _ledger.GetTrialBalance(c.Id).Where(r => r.Account.Type == AccountType.Pasivo).Sum(r => r.Balance);
        decimal unpaid = TaxesOf(c).Unpaid.Sum(o => (decimal)o.Total);
        decimal freeCash = Cash(c) - Math.Max(liabilities, unpaid) - CapitalOf(c) - _ledger.GetBalance(c.Id, SystemAccounts.PrimaDeEmision);
        return Math.Max(0, Math.Floor(Math.Min(RetainedEarnings(c), freeCash)));
    }

    /// <summary>Facturación de las últimas 4 estaciones (incluida la actual).</summary>
    public decimal AnnualRevenue(CompanyState c)
    {
        TaxState t = c.Taxes;
        return t.RevenueHistory.Skip(Math.Max(0, t.RevenueHistory.Count - 3)).Sum(r => r.NetSales) + t.Current.NetSales + t.Current.Exports;
    }

    // ───────────────────────────── Alta y operaciones del socio ─────────────────────────────

    public CompanyError CanCreate(string type, string form, decimal capital, bool unlocked, decimal pocket)
    {
        if (Rules.FindType(type) is null)
        {
            return CompanyError.UnknownType;
        }

        if (Rules.FindForm(form) is null)
        {
            return CompanyError.UnknownForm;
        }

        if (!unlocked && !_s.UnlockAll)
        {
            return CompanyError.Locked;
        }

        if (ActiveOfType(type) is not null)
        {
            return CompanyError.AlreadyExists;
        }

        if (_legal.IsInhibited)
        {
            return CompanyError.Inhibited;
        }

        if (capital < MinimumCapital(form))
        {
            return CompanyError.BelowMinimumCapital;
        }

        return pocket < capital + RegistrationFee(form) ? CompanyError.InsufficientFunds : CompanyError.None;
    }

    /// <summary>Constituye una sociedad: el jugador integra el capital y paga la inscripción.</summary>
    public CompanyResult Create(string type, string form, decimal capital, bool unlocked, decimal pocket, EconomicDate date, List<CompanyNotice> notices)
    {
        capital = Math.Floor(capital);
        CompanyError error = CanCreate(type, form, capital, unlocked, pocket);
        if (error != CompanyError.None)
        {
            return new CompanyResult(error);
        }

        CompanyTypeDefinition t = Rules.FindType(type)!;
        int n = _s.NextCompany++;
        var company = new CompanyState
        {
            Id = LedgerEntities.CompanyPrefix + n.ToString(CultureInfo.InvariantCulture),
            Type = t.Id,
            Form = Rules.FindForm(form)!.Id,
            Cuit = string.Create(CultureInfo.InvariantCulture, $"30-{70_000_000 + (n * 7_919):D8}-{n % 10}"),
            CreatedOn = date,
        };
        _s.Companies.Add(company);
        Attach(company);
        AssignTicker(company);

        decimal fee = RegistrationFee(form);
        PostPlayer(date, $"Constitución de {company.Id}", "entry.company-created",
            JournalLine.ToDebit(SystemAccounts.ParticipacionesEnSociedades, capital),
            JournalLine.ToDebit(SystemAccounts.TasasOtras, fee),
            JournalLine.ToCredit(SystemAccounts.Caja, capital + fee));
        company.Contributed = capital;
        PostCompany(company, date, "Integración del capital social", "entry.company-capital",
            JournalLine.ToDebit(SystemAccounts.Caja, capital),
            JournalLine.ToCredit(SystemAccounts.Capital, capital));

        var taxNotices = new List<TaxNotice>();
        TaxesOf(company).Start(date, FarmSnapshot.Empty, Ipc);
        notices.Add(new CompanyNotice(CompanyNoticeKind.Created, date, company.Id, capital, company.Type));
        return new CompanyResult(CompanyError.None, -(capital + fee), company);
    }

    public CompanyResult Contribute(string id, decimal amount, decimal pocket, EconomicDate date)
    {
        amount = Math.Floor(amount);
        if (Find(id) is not { Active: true } c)
        {
            return new CompanyResult(CompanyError.NotFound);
        }

        if (amount < 1 || amount > pocket)
        {
            return new CompanyResult(CompanyError.InsufficientFunds);
        }

        Contribute(c, amount, date);
        return new CompanyResult(CompanyError.None, -amount, c);
    }

    public CompanyResult PayDividend(string id, decimal amount, EconomicDate date)
    {
        amount = Math.Floor(amount);
        if (Find(id) is not { Active: true } c)
        {
            return new CompanyResult(CompanyError.NotFound);
        }

        if (amount < 1 || amount > Distributable(c))
        {
            return new CompanyResult(CompanyError.NothingToDistribute);
        }

        // Si cotiza, a vos te toca tu porcentaje; el resto va a los accionistas minoritarios.
        decimal own = c.Listing is { } listing ? MoneyRounding.Round(amount * listing.PlayerFraction) : amount;
        decimal withheld = MoneyRounding.Round(own * _data.Markets.DividendWithholding);
        PostCompany(c, date, "Distribución de dividendos", "entry.company-dividend",
            JournalLine.ToDebit(SystemAccounts.ResultadosAcumulados, amount),
            JournalLine.ToCredit(SystemAccounts.Caja, amount));
        if (own > 0)
        {
            PostPlayer(date, $"Dividendos de {c.Id}", "entry.company-dividend-received",
                JournalLine.ToDebit(SystemAccounts.Caja, own - withheld),
                JournalLine.ToDebit(SystemAccounts.RetencionDividendos, withheld),
                JournalLine.ToCredit(SystemAccounts.Dividendos, own));
        }

        c.DividendsPaid += own;
        return new CompanyResult(CompanyError.None, own - withheld, c);
    }

    /// <summary>Transforma una S.R.L. en S.A.: paga la tasa y completa el capital mínimo con aportes.</summary>
    public CompanyResult Transform(string id, string form, decimal pocket, EconomicDate date, List<CompanyNotice> notices)
    {
        if (Find(id) is not { Active: true } c)
        {
            return new CompanyResult(CompanyError.NotFound);
        }

        if (Rules.FindForm(form) is not { } target || target.Id.Equals(c.Form, StringComparison.OrdinalIgnoreCase))
        {
            return new CompanyResult(CompanyError.UnknownForm);
        }

        decimal topUp = Math.Max(0, MinimumCapital(target.Id) - CapitalOf(c));
        decimal fee = TransformationFee;
        if (pocket < topUp + fee)
        {
            return new CompanyResult(CompanyError.InsufficientFunds);
        }

        PostPlayer(date, $"Transformación de {c.Id} en {target.Id}", "entry.company-transformed",
            JournalLine.ToDebit(SystemAccounts.TasasOtras, fee),
            JournalLine.ToCredit(SystemAccounts.Caja, fee));
        if (topUp > 0)
        {
            Contribute(c, topUp, date);
        }

        c.Form = target.Id;
        AssignTicker(c);
        notices.Add(new CompanyNotice(CompanyNoticeKind.Transformed, date, c.Id, topUp + fee, target.Id));
        return new CompanyResult(CompanyError.None, -(topUp + fee), c);
    }

    /// <summary>
    /// Disuelve una sociedad: paga todas sus deudas con su caja y le devuelve el remanente al socio.
    /// No se puede con cobros pendientes (ventas a rendir o exportaciones a cobrar).
    /// </summary>
    public CompanyResult Dissolve(string id, EconomicDate date, List<CompanyNotice> notices)
    {
        if (Find(id) is not { Active: true } c)
        {
            return new CompanyResult(CompanyError.NotFound);
        }

        if (_ledger.GetBalance(c.Id, SystemAccounts.CobrosARendirPorSocio) != 0 || _ledger.GetBalance(c.Id, SystemAccounts.ExportacionesACobrar) != 0
            || c.Listing is not null || _ledger.GetBalance(c.Id, SystemAccounts.OnEmitidas) != 0)
        {
            return new CompanyResult(CompanyError.NotAllowed);
        }

        TaxEngine taxes = TaxesOf(c);
        foreach (TaxObligation o in taxes.Unpaid.ToList())
        {
            taxes.PayFrom(o.Id, o.Total, date, SystemAccounts.Caja);
        }

        // Liquidación final: pasivos que quedan (IVA del período, impuestos devengados) contra caja.
        List<JournalLine> settle = new();
        decimal liabilities = 0;
        foreach (TrialBalanceRow row in _ledger.GetTrialBalance(c.Id).Where(r => r.Account.Type == AccountType.Pasivo && r.Balance != 0))
        {
            settle.Add(JournalLine.ToDebit(row.Account.Code, row.Balance));
            liabilities += row.Balance;
        }

        if (Cash(c) < liabilities || taxes.Unpaid.Any())
        {
            return new CompanyResult(CompanyError.InsufficientFunds);
        }

        if (settle.Count > 0)
        {
            settle.Add(JournalLine.ToCredit(SystemAccounts.Caja, liabilities));
            PostCompany(c, date, "Liquidación final de deudas", "entry.company-final-settlement", settle.ToArray());
        }

        WriteOffOtherAssets(c, date);
        decimal remainder = Cash(c);
        Close(c, date, remainder);

        decimal cost = c.Contributed;
        var lines = new List<JournalLine>();
        if (remainder > 0)
        {
            lines.Add(JournalLine.ToDebit(SystemAccounts.Caja, remainder));
        }

        if (cost > 0)
        {
            lines.Add(JournalLine.ToCredit(SystemAccounts.ParticipacionesEnSociedades, cost));
        }

        if (remainder > cost)
        {
            lines.Add(JournalLine.ToCredit(SystemAccounts.ResultadosVentaPositivos, remainder - cost));
        }
        else if (remainder < cost)
        {
            lines.Add(JournalLine.ToDebit(SystemAccounts.ResultadosVentaNegativos, cost - remainder));
        }

        if (lines.Count > 1)
        {
            PostPlayer(date, $"Disolución de {c.Id}", "entry.company-dissolved", lines.ToArray());
        }

        c.Active = false;
        c.DissolvedOn = date;
        c.Contributed = 0;
        notices.Add(new CompanyNotice(CompanyNoticeKind.Dissolved, date, c.Id, remainder));
        return new CompanyResult(CompanyError.None, remainder, c);
    }

    // ───────────────────────────── Ventas ─────────────────────────────

    public CompanyState? CompanyForActivity(string activity) =>
        Active.FirstOrDefault(c => Rules.FindType(c.Type)?.Activities.Contains(activity, StringComparer.OrdinalIgnoreCase) == true);

    /// <summary>Bonificación del precio de venta si una sociedad absorbe esa actividad.</summary>
    public decimal PriceBonus(string activity) =>
        CompanyForActivity(activity) is { } c ? Rules.FindType(c.Type)?.PriceBonus ?? 0 : 0;

    /// <summary>
    /// Asienta en cada sociedad las ventas de su rubro y devuelve las que quedan para el jugador.
    /// El oro lo cobra el jugador y lo rinde a la sociedad al día siguiente.
    /// </summary>
    public List<SoldItem> Attribute(IReadOnlyCollection<SoldItem> items, SaleChannel channel, EconomicDate date)
    {
        var rest = new List<SoldItem>();
        foreach (IGrouping<string, SoldItem> group in items.Where(i => i.Gross > 0).GroupBy(i => _playerTaxes.ActivityFor(i.ItemId, i.Category)))
        {
            if (CompanyForActivity(group.Key) is not { } c)
            {
                rest.AddRange(group);
                continue;
            }

            long gross = group.Sum(i => i.Gross);
            TaxesOf(c).RecordSale(date, group.Key, gross, channel, SystemAccounts.CobrosARendirPorSocio);
            PostPlayer(date, $"Cobro por cuenta de {c.Id}", "entry.company-collected-for",
                JournalLine.ToDebit(channel == SaleChannel.Shipping ? SystemAccounts.DeudoresPorVentas : SystemAccounts.Caja, gross),
                JournalLine.ToCredit(SystemAccounts.CobranzasARendirASociedades, gross));
        }

        return rest;
    }

    /// <summary>Rinde a cada sociedad lo cobrado por su cuenta, hasta <paramref name="budget"/> de oro. Devuelve lo transferido.</summary>
    public decimal TransferCollections(EconomicDate date, decimal? budget)
    {
        decimal total = 0;
        foreach (CompanyState c in Active)
        {
            decimal pending = _ledger.GetBalance(c.Id, SystemAccounts.CobrosARendirPorSocio);
            decimal amount = Math.Floor(budget is decimal b ? Math.Min(pending, b - total) : pending);
            if (amount < 1)
            {
                continue;
            }

            PostPlayer(date, $"Rendición de cobranzas a {c.Id}", "entry.company-transfer",
                JournalLine.ToDebit(SystemAccounts.CobranzasARendirASociedades, amount),
                JournalLine.ToCredit(SystemAccounts.Caja, amount));
            PostCompany(c, date, "Cobranzas rendidas por el socio", "entry.company-transfer-received",
                JournalLine.ToDebit(SystemAccounts.Caja, amount),
                JournalLine.ToCredit(SystemAccounts.CobrosARendirPorSocio, amount));
            total += amount;
        }

        return total;
    }

    // ───────────────────────────── Exportación ─────────────────────────────

    public void AddToContainer(string itemId, int quality, int stack, int basePrice)
    {
        SiloItem? existing = _s.ExportContainer.FirstOrDefault(i => i.ItemId == itemId && i.Quality == quality);
        if (existing is not null)
        {
            existing.Stack += stack;
        }
        else
        {
            _s.ExportContainer.Add(new SiloItem { ItemId = itemId, Quality = quality, Stack = stack, BasePrice = basePrice });
        }
    }

    public void SetContainer(IEnumerable<SiloItem> items)
    {
        _s.ExportContainer.Clear();
        _s.ExportContainer.AddRange(items.Where(i => i.Stack > 0));
    }

    /// <summary>Valor FOB y retención de un objeto exportable (null si no es un commodity).</summary>
    public (decimal Fob, decimal DutyRate)? Quote(SiloItem item, double qualityPremium)
    {
        if (_data.FindCommodityByItemId(item.ItemId) is not { } commodity)
        {
            return null;
        }

        decimal unit = item.BasePrice * (decimal)(_market.GetMultiplierForItem(item.ItemId) * (1 + qualityPremium) * ExportFactor()) * (1 + Trade.GotoroPremium);
        return (unit, Trade.ExportDuty(commodity.Id, _difficulty().TradeDuties));
    }

    /// <summary>Días hábiles: exporta todo el contenedor por la Comercializadora (cobro a N días).</summary>
    public void Export(EconomicDate date, Func<int, double> qualityPremium, List<CompanyNotice> notices)
    {
        if (!date.IsBusinessDay || Exporter is not { } exporter || _s.ExportContainer.Count == 0)
        {
            return;
        }

        decimal fob = 0;
        decimal duty = 0;
        foreach (SiloItem item in _s.ExportContainer.ToList())
        {
            if (Quote(item, qualityPremium(item.Quality)) is not { } q)
            {
                continue;
            }

            decimal value = q.Fob * item.Stack;
            fob += value;
            duty += value * q.DutyRate;
            _market.RecordShipment(item.ItemId, (int)Math.Round(item.Stack * Trade.ExportSupplyFactor));
            _s.ExportContainer.Remove(item);
        }

        fob = MoneyRounding.Round(fob);
        duty = MoneyRounding.Round(duty);
        if (fob <= 0)
        {
            return;
        }

        TaxesOf(exporter).RecordExport(date, fob, duty, new EntryText("Exportación a Gotoro", "entry.export"));
        _s.Receivables.Add(new ExportReceivable
        {
            Id = _s.NextReceivable++,
            Company = exporter.Id,
            ExportedOn = date,
            CollectOn = date.AddDays(Trade.ExportCollectionDays).NextBusinessDayOrSelf(),
            Amount = fob - duty,
        });
        notices.Add(new CompanyNotice(CompanyNoticeKind.Exported, date, exporter.Id, fob - duty, duty.ToString("0", CultureInfo.InvariantCulture)));
    }

    // ───────────────────────────── Día a día ─────────────────────────────

    public void OnPeriodStart(EconomicDate date, List<CompanyNotice> notices)
    {
        FlushPending(notices);
        foreach (CompanyState c in Active.ToList())
        {
            c.ProfitSnapshots.Add(CumulativeProfit(c));
            TaxEngine taxes = TaxesOf(c);
            var taxNotices = new List<TaxNotice>();
            taxNotices.AddRange(taxes.OnPeriodStart(date, FarmSnapshot.Empty, Ipc));
            if (c.CreatedOn.FiscalPeriod != date.FiscalPeriod)
            {
                taxes.IssueFee(TaxKind.TasaFiscalizacion, Rules.SeasonalFee * Ipc * _difficulty().TaxBurden, date, SystemAccounts.TasasOtras, taxNotices);
            }

            UpdateRating(c, date);
        }
    }

    public void OnDay(EconomicDate date, Func<int, double> qualityPremium, List<CompanyNotice> notices)
    {
        FlushPending(notices);
        foreach (ExportReceivable r in _s.Receivables.Where(r => r.CollectOn <= date).ToList())
        {
            if (Find(r.Company) is { } c)
            {
                PostCompany(c, date, "Cobro de exportación", "entry.export-collected",
                    JournalLine.ToDebit(SystemAccounts.Caja, r.Amount),
                    JournalLine.ToCredit(SystemAccounts.ExportacionesACobrar, r.Amount));
                notices.Add(new CompanyNotice(CompanyNoticeKind.ExportCollected, date, c.Id, r.Amount));
            }

            _s.Receivables.Remove(r);
        }

        Export(date, qualityPremium, notices);

        foreach (CompanyState c in Active.ToList())
        {
            TaxEngine taxes = TaxesOf(c);
            taxes.PayDueFromCash(date);
            foreach (TaxNotice n in taxes.OnDay(date))
            {
                if (n.Kind == TaxNoticeKind.Overdue)
                {
                    notices.Add(new CompanyNotice(CompanyNoticeKind.TaxesOverdue, date, c.Id, taxes.Find(n.ObligationId ?? 0)?.Total ?? 0));
                }
                else if (n.Kind == TaxNoticeKind.InCollection)
                {
                    notices.Add(new CompanyNotice(CompanyNoticeKind.InCollection, date, c.Id, taxes.Find(n.ObligationId ?? 0)?.Total ?? 0));
                }
            }

            taxes.PayDueFromCash(date);
            CheckInsolvency(c, date, notices);
        }
    }

    private void CheckInsolvency(CompanyState c, EconomicDate date, List<CompanyNotice> notices)
    {
        TaxEngine taxes = TaxesOf(c);
        if (!taxes.InCollection.Any())
        {
            c.CollectionSince = null;
            return;
        }

        c.CollectionSince ??= date;
        int threshold = (int)Math.Ceiling(Rules.InsolvencyAfterDays / Math.Max(0.1m, _difficulty().BankruptcySpeed));
        if (c.CollectionSince.Value.DaysUntil(date) >= threshold)
        {
            Bankrupt(c, date, notices);
        }
    }

    /// <summary>Quiebra de la sociedad: su caja va a los acreedores, el resto se condona y se disuelve. El socio pierde su aporte.</summary>
    public void Bankrupt(CompanyState c, EconomicDate date, List<CompanyNotice> notices)
    {
        TaxEngine taxes = TaxesOf(c);
        foreach (TaxObligation o in taxes.Unpaid.OrderBy(o => o.DueDate).ToList())
        {
            decimal cash = Cash(c);
            if (cash >= 1)
            {
                taxes.PayFrom(o.Id, cash, date, SystemAccounts.Caja);
            }

            if (!o.IsPaid)
            {
                taxes.Close(o.Id, date, SystemAccounts.QuitaConcursal);
            }
        }

        var condone = new List<JournalLine>();
        decimal condoned = 0;
        foreach (TrialBalanceRow row in _ledger.GetTrialBalance(c.Id).Where(r => r.Account.Type == AccountType.Pasivo && r.Balance > 0))
        {
            condone.Add(JournalLine.ToDebit(row.Account.Code, row.Balance));
            condoned += row.Balance;
        }

        if (condoned > 0)
        {
            condone.Add(JournalLine.ToCredit(SystemAccounts.QuitaConcursal, condoned));
            PostCompany(c, date, "Condonación por quiebra de la sociedad", "entry.company-bankrupt", condone.ToArray());
        }

        // Los cobros pendientes se cancelan contra el socio (el oro no llegó a la sociedad).
        decimal pending = _ledger.GetBalance(c.Id, SystemAccounts.CobrosARendirPorSocio);
        if (pending > 0)
        {
            PostCompany(c, date, "Cobranzas pendientes del socio", "entry.company-bankrupt",
                JournalLine.ToDebit(SystemAccounts.QuitaConcursal, pending),
                JournalLine.ToCredit(SystemAccounts.CobrosARendirPorSocio, pending));
            PostPlayer(date, $"Cobranzas de {c.Id} que quedan para el socio", "entry.company-bankrupt",
                JournalLine.ToDebit(SystemAccounts.CobranzasARendirASociedades, pending),
                JournalLine.ToCredit(SystemAccounts.IngresosNoClasificados, pending));
        }

        _s.Receivables.RemoveAll(r => r.Company == c.Id);
        if (c.Ticker is { } ticker)
        {
            if (_credit.Issuers.TryGetValue(ticker, out IssuerState? issuer))
            {
                issuer.Defaulted = true;
                issuer.DefaultedIn = date.FiscalPeriod;
                issuer.Rating = "D";
            }

            _broker.WriteOffIssuer(ticker, date);
            if (c.Listing is not null)
            {
                _stocks.Delist(ticker);
                c.Listing = null;
            }
        }

        WriteOffOtherAssets(c, date, includeCash: true);
        Close(c, date, 0);
        if (c.Contributed > 0)
        {
            PostPlayer(date, $"Pérdida de la participación en {c.Id}", "entry.company-bankrupt",
                JournalLine.ToDebit(SystemAccounts.ResultadosVentaNegativos, c.Contributed),
                JournalLine.ToCredit(SystemAccounts.ParticipacionesEnSociedades, c.Contributed));
        }

        c.Contributed = 0;
        c.Active = false;
        c.Bankrupt = true;
        c.DissolvedOn = date;
        c.Rating = "D";
        _legal.State.Events.Add(new LegalEvent(date, LegalEventKind.CompanyBankruptcy));
        notices.Add(new CompanyNotice(CompanyNoticeKind.Bankrupt, date, c.Id, condoned));
    }

    private void UpdateRating(CompanyState c, EconomicDate date)
    {
        double revenue = Math.Max(1000, (double)AnnualRevenue(c));
        int overdue = c.Taxes.Obligations.Count(o => o.Status != ObligationStatus.Pending && o.DueDate.DaysUntil(date) <= EconomicDate.DaysPerYear && (o.PaidOn is null || o.PaidOn > o.DueDate));
        double score = 55 + Math.Clamp((double)Equity(c) / revenue * 20, -20, 25) - Math.Min(30, 6 * overdue) - (c.CollectionSince is null ? 0 : 20);
        c.Score = score;
        c.Rating = score >= 85 ? "AAA" : score >= 75 ? "AA" : score >= 65 ? "A" : score >= 55 ? "BBB"
            : score >= 45 ? "BB" : score >= 35 ? "B" : score >= 20 ? "CCC" : "D";
        if (c.Ticker is { } ticker && _credit.Issuers.TryGetValue(ticker, out IssuerState? issuer) && !issuer.Defaulted)
        {
            issuer.Rating = c.Rating;
        }
    }

    // ───────────────────────────── Internos ─────────────────────────────

    private void Attach(CompanyState c)
    {
        _ledger.AddEntity(c.Id);
        _taxes[c.Id] = new TaxEngine(_data, c.Taxes, _ledger, _difficulty(), c.Id, company: true, nextId: () => _playerTaxes.State.NextObligationId++);
        if (c.Active)
        {
            AssignTicker(c);
        }
    }

    private void FlushPending(List<CompanyNotice> notices)
    {
        notices.AddRange(_pending);
        _pending.Clear();
    }

    private void Contribute(CompanyState c, decimal amount, EconomicDate date)
    {
        PostPlayer(date, $"Aporte de capital a {c.Id}", "entry.company-contribution",
            JournalLine.ToDebit(SystemAccounts.ParticipacionesEnSociedades, amount),
            JournalLine.ToCredit(SystemAccounts.Caja, amount));
        PostCompany(c, date, "Aporte de capital del socio", "entry.company-capital",
            JournalLine.ToDebit(SystemAccounts.Caja, amount),
            JournalLine.ToCredit(SystemAccounts.Capital, amount));
        c.Contributed += amount;
    }

    /// <summary>Da de baja activos que no son caja (IVA a favor, anticipos) al cerrar la sociedad.</summary>
    private void WriteOffOtherAssets(CompanyState c, EconomicDate date, bool includeCash = false)
    {
        var lines = new List<JournalLine>();
        decimal total = 0;
        foreach (TrialBalanceRow row in _ledger.GetTrialBalance(c.Id).Where(r => r.Account.Type == AccountType.Activo && r.Balance != 0
            && (includeCash || r.Account.Code != SystemAccounts.Caja)))
        {
            lines.Add(JournalLine.ToCredit(row.Account.Code, row.Balance));
            total += row.Balance;
        }

        if (total != 0)
        {
            lines.Add(JournalLine.ToDebit(SystemAccounts.EgresosNoClasificados, total));
            PostCompany(c, date, "Baja de activos al cerrar la sociedad", "entry.company-writeoff", lines.ToArray());
        }
    }

    /// <summary>Cierra el patrimonio: el capital se devuelve (remanente) y la diferencia va a resultados acumulados.</summary>
    private void Close(CompanyState c, EconomicDate date, decimal remainder)
    {
        decimal capital = CapitalOf(c);
        var lines = new List<JournalLine>();
        if (capital != 0)
        {
            lines.Add(JournalLine.ToDebit(SystemAccounts.Capital, capital));
        }

        if (remainder > 0)
        {
            lines.Add(JournalLine.ToCredit(SystemAccounts.Caja, remainder));
        }

        decimal diff = capital - remainder;
        if (diff > 0)
        {
            lines.Add(JournalLine.ToCredit(SystemAccounts.ResultadosAcumulados, diff));
        }
        else if (diff < 0)
        {
            lines.Add(JournalLine.ToDebit(SystemAccounts.ResultadosAcumulados, -diff));
        }

        if (lines.Count > 1)
        {
            PostCompany(c, date, "Cierre del patrimonio de la sociedad", "entry.company-closed", lines.ToArray());
        }
    }

    private void PostPlayer(EconomicDate date, string fallback, string key, params JournalLine[] lines) =>
        _ledger.Post(Player, date, new EntryText(fallback, key), EntrySources.Ajuste, lines);

    private void PostCompany(CompanyState c, EconomicDate date, string fallback, string key, params JournalLine[] lines) =>
        _ledger.Post(c.Id, date, new EntryText(fallback, key), EntrySources.Ajuste, lines);
}
