using System.Globalization;
using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Ledger;
using ZuzuMarket.Core.Macro;
using ZuzuMarket.Core.Markets;
using ZuzuMarket.Core.Money;
using ZuzuMarket.Core.Taxation;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Legal;

/// <summary>Lo que pasó hoy en lo legal y lo que el juego tiene que hacer (descontar oro, secuestrar bienes).</summary>
public sealed class LegalDay
{
    public List<LegalNotice> Notices { get; } = new();

    /// <summary>Oro de los cobros del cajón embargado hoy: el juego lo descuenta del bolsillo.</summary>
    public decimal Garnished { get; set; }

    /// <summary>Deuda a cubrir secuestrando objetos de los cofres (0 = nada).</summary>
    public decimal SeizureRequest { get; set; }

    /// <summary>Se declaró la quiebra: el juego remata bienes (y edificios, si corresponde) y llama a FinalizeBankruptcy.</summary>
    public bool BankruptcyDeclared { get; set; }
}

/// <summary>
/// Embargos, inhibición, concurso preventivo, quiebra, préstamos del Banco de Zuzu y calificación crediticia del
/// jugador (§10.5 y §11 del diseño).
/// </summary>
public sealed class LegalEngine
{
    private const string Entity = LedgerEntities.Jugador;
    private const string Embargo_Accounts = "cuentas";
    private const string Embargo_Income = "ingresos";
    private const string Embargo_Goods = "bienes";
    public const string Embargo_Buildings = "edificios";

    private readonly GameData _data;
    private readonly LegalState _s;
    private readonly GeneralLedger _ledger;
    private readonly TaxEngine _taxes;
    private readonly Broker _broker;
    private readonly CentralBank _centralBank;
    private readonly Func<DifficultyDefinition> _difficulty;

    public LegalEngine(GameData data, LegalState state, GeneralLedger ledger, TaxEngine taxes, Broker broker, CentralBank centralBank, Func<DifficultyDefinition> difficulty)
    {
        _data = data;
        _s = state;
        _ledger = ledger;
        _taxes = taxes;
        _broker = broker;
        _centralBank = centralBank;
        _difficulty = difficulty;
        broker.IsInhibited = () => IsInhibited;
        broker.IsAccountFrozen = () => IsEmbargoActive && HasEmbargo(Embargo_Accounts);
    }

    public LegalRules Rules => _data.Legal;

    public LegalState State => _s;

    public string Rating => _s.Rating;

    public bool IsEmbargoActive => _s.EmbargoSince is not null;

    public bool IsInhabilitado(EconomicDate date) => _s.InhabilitadoUntil is EconomicDate until && date < until;

    /// <summary>Inhibición: con embargo, concurso, quiebra pendiente o inhabilitación no se abren posiciones ni se pide crédito.</summary>
    public bool IsInhibited => IsEmbargoActive || _s.ConcursoActive || _s.BankruptcyPending || _s.InhabilitadoUntil is not null;

    public bool HasEmbargo(string kind) => _difficulty().Embargoes.Contains(kind, StringComparer.OrdinalIgnoreCase);

    // ───────────────────────────── Deudas ─────────────────────────────

    /// <summary>Saldo negativo de la cuenta comitente: deuda con el ALyC.</summary>
    public decimal BrokerDebt => Math.Max(0, -_broker.Cash);

    /// <summary>Deuda exigible: boletas en gestión de cobro (incluidas cuotas) más el saldo negativo con el ALyC.</summary>
    public decimal DebtInCollection => _taxes.InCollection.Sum(o => (decimal)o.Total) + BrokerDebt;

    /// <summary>Patrimonio neto contable: activos − pasivos.</summary>
    public decimal NetWorth
    {
        get
        {
            decimal assets = 0;
            decimal liabilities = 0;
            foreach (TrialBalanceRow row in _ledger.GetTrialBalance(Entity))
            {
                if (row.Account.Type == AccountType.Activo)
                {
                    assets += row.Balance;
                }
                else if (row.Account.Type == AccountType.Pasivo)
                {
                    liabilities += row.Balance;
                }
            }

            return assets - liabilities;
        }
    }

    /// <summary>Facturación de las últimas 4 estaciones (incluida la actual).</summary>
    public decimal AnnualRevenue
    {
        get
        {
            List<PeriodRevenue> history = _taxes.State.RevenueHistory;
            return history.Skip(Math.Max(0, history.Count - 3)).Sum(r => r.NetSales) + _taxes.State.Current.NetSales;
        }
    }

    /// <summary>Aplica hasta <paramref name="amount"/> a las deudas exigibles, de la más vieja a la más nueva.</summary>
    public decimal PayDebts(decimal amount, string sourceAccount, EconomicDate date)
    {
        decimal paid = 0;
        foreach (TaxObligation o in _taxes.InCollection.OrderBy(o => o.DueDate).ThenBy(o => o.Id))
        {
            if (amount - paid < 1)
            {
                break;
            }

            paid += _taxes.PayFrom(o.Id, amount - paid, date, sourceAccount);
        }

        decimal brokerDebt = BrokerDebt;
        if (brokerDebt > 0 && amount - paid > 0 && sourceAccount != SystemAccounts.CuentaComitente)
        {
            decimal pay = MoneyRounding.Round(Math.Min(brokerDebt, amount - paid));
            Post(date, "Pago de la deuda con el ALyC", "entry.broker-debt-paid",
                JournalLine.ToDebit(SystemAccounts.CuentaComitente, pay),
                JournalLine.ToCredit(sourceAccount, pay));
            paid += pay;
        }

        return paid;
    }

    // ───────────────────────────── Día a día ─────────────────────────────

    /// <summary>Registra atrasos e intimaciones para la calificación.</summary>
    public void RecordTaxNotices(IEnumerable<TaxNotice> notices)
    {
        foreach (TaxNotice n in notices)
        {
            if (n.Kind == TaxNoticeKind.Overdue)
            {
                _s.Events.Add(new LegalEvent(n.Date, LegalEventKind.Overdue));
            }
            else if (n.Kind == TaxNoticeKind.Intimated)
            {
                _s.Events.Add(new LegalEvent(n.Date, LegalEventKind.Intimation));
            }
        }
    }

    public void OnDay(EconomicDate date, LegalDay day)
    {
        DifficultyDefinition difficulty = _difficulty();

        if (_s.BankruptcyPending)
        {
            // El juego no llegó a rematar bienes (p. ej. se saltearon días): se cierra igual.
            FinalizeBankruptcy(date, day);
        }

        if (_s.InhabilitadoUntil is EconomicDate until && date >= until)
        {
            _s.InhabilitadoUntil = null;
            day.Notices.Add(new LegalNotice(LegalNoticeKind.Rehabilitated, date));
        }

        if (_s.ConcursoActive)
        {
            List<TaxObligation> cuotas = _taxes.State.Obligations.Where(o => o.Kind == TaxKind.CuotaConcurso && !o.IsPaid).ToList();
            if (cuotas.Count == 0)
            {
                _s.ConcursoActive = false;
                day.Notices.Add(new LegalNotice(LegalNoticeKind.ConcursoCompleted, date));
            }
            else if (cuotas.Any(o => o.Status == ObligationStatus.InCollection) && difficulty.BankruptcyEnabled)
            {
                DeclareBankruptcy(date, day);
                return;
            }
        }

        decimal debt = DebtInCollection;
        if (debt < 1)
        {
            if (IsEmbargoActive)
            {
                day.Notices.Add(new LegalNotice(LegalNoticeKind.EmbargoLifted, date));
            }

            _s.EmbargoSince = null;
            _s.CollectionSince = null;
            _s.LastSeizure = null;
            return;
        }

        _s.CollectionSince ??= date;
        int days = _s.CollectionSince.Value.DaysUntil(date);
        if (!IsEmbargoActive && days >= Threshold(Rules.EmbargoAfterDays) && difficulty.Embargoes.Count > 0)
        {
            _s.EmbargoSince = date;
            _s.Events.Add(new LegalEvent(date, LegalEventKind.Embargo));
            day.Notices.Add(new LegalNotice(LegalNoticeKind.EmbargoStarted, date, debt));
        }

        if (IsEmbargoActive)
        {
            if (HasEmbargo(Embargo_Accounts) && _broker.Cash >= 1)
            {
                decimal paid = PayDebts(Math.Floor(_broker.Cash), SystemAccounts.CuentaComitente, date);
                if (paid > 0)
                {
                    day.Notices.Add(new LegalNotice(LegalNoticeKind.AccountSeized, date, paid));
                }
            }

            decimal receivable = _ledger.GetBalance(Entity, SystemAccounts.DeudoresPorVentas);
            if (HasEmbargo(Embargo_Income) && receivable >= 1)
            {
                decimal paid = PayDebts(Math.Floor(receivable * Rules.IncomeGarnishRate), SystemAccounts.DeudoresPorVentas, date);
                if (paid > 0)
                {
                    day.Garnished += paid;
                    day.Notices.Add(new LegalNotice(LegalNoticeKind.IncomeGarnished, date, paid));
                }
            }

            if (HasEmbargo(Embargo_Goods) && days >= Threshold(Rules.SeizureAfterDays)
                && (_s.LastSeizure is null || _s.LastSeizure.Value.DaysUntil(date) >= Rules.SeizureEveryDays))
            {
                _s.LastSeizure = date;
                SeizeSilo(date, day);
                decimal remaining = DebtInCollection;
                if (remaining >= 1)
                {
                    day.SeizureRequest = remaining;
                    day.Notices.Add(new LegalNotice(LegalNoticeKind.SeizureRequested, date, remaining));
                }
            }
        }

        if (difficulty.BankruptcyEnabled && days >= Threshold(Rules.BankruptcyAfterDays) && DebtInCollection >= 1)
        {
            DeclareBankruptcy(date, day);
        }
    }

    /// <summary>El juego remató objetos (o edificios) por <paramref name="value"/>: se aplica a las deudas.</summary>
    public decimal ApplySeizure(decimal value, EconomicDate date, LegalDay day)
    {
        decimal paid = value >= 1 ? PayDebts(value, SystemAccounts.IngresosPorRemate, date) : 0;
        if (paid > 0)
        {
            day.Notices.Add(new LegalNotice(LegalNoticeKind.GoodsSeized, date, paid));
        }

        return paid;
    }

    // ───────────────────────────── Concurso y quiebra ─────────────────────────────

    public Markets.TradeError CanRequestConcurso(EconomicDate date)
    {
        if (_s.ConcursoActive || _s.BankruptcyPending || IsInhabilitado(date))
        {
            return Markets.TradeError.Inhibited;
        }

        return DebtInCollection >= 1 || _taxes.Unpaid.Any(o => o.DueDate < date) ? Markets.TradeError.None : Markets.TradeError.InvalidQuantity;
    }

    /// <summary>
    /// Concurso preventivo: las deudas vencidas pasan a la masa concursal con una quita según la calificación y se
    /// pagan en cuotas por estación. Se levanta el embargo y se congelan los intereses.
    /// </summary>
    public LegalNotice? RequestConcurso(EconomicDate date, List<TaxNotice> taxNotices)
    {
        if (CanRequestConcurso(date) != Markets.TradeError.None)
        {
            return null;
        }

        decimal total = 0;
        foreach (TaxObligation o in _taxes.Unpaid.Where(o => o.DueDate < date && o.Kind != TaxKind.CuotaConcurso).ToList())
        {
            total += _taxes.Close(o.Id, date, SystemAccounts.DeudaConcursal);
        }

        decimal brokerDebt = BrokerDebt;
        if (brokerDebt > 0)
        {
            Post(date, "Deuda con el ALyC al concurso", "entry.concurso-broker",
                JournalLine.ToDebit(SystemAccounts.CuentaComitente, brokerDebt),
                JournalLine.ToCredit(SystemAccounts.DeudaConcursal, brokerDebt));
            total += brokerDebt;
        }

        long haircut = MoneyRounding.ToGold(total * Rules.Haircut(_s.Rating));
        if (haircut > 0)
        {
            Post(date, "Quita del concurso preventivo", "entry.concurso-haircut",
                JournalLine.ToDebit(SystemAccounts.DeudaConcursal, haircut),
                JournalLine.ToCredit(SystemAccounts.QuitaConcursal, haircut));
        }

        long toPay = MoneyRounding.ToGold(total) - haircut;
        int n = Math.Max(1, Rules.ConcursoInstallments);
        long each = toPay / n;
        for (int k = 1; k <= n; k++)
        {
            long amount = k == n ? toPay - (each * (n - 1)) : each;
            if (amount <= 0)
            {
                continue;
            }

            FiscalPeriod period = date.FiscalPeriod.AddSeasons(k);
            EconomicDate due = new EconomicDate(period.Year, period.Season, _taxes.Rules.DueDay).NextBusinessDayOrSelf();
            _taxes.CreateExternal(TaxKind.CuotaConcurso, period, amount, date, due, taxNotices);
        }

        // Diferencia de redondeo entre el total y lo que se cuotificó.
        decimal residue = _ledger.GetBalance(Entity, SystemAccounts.DeudaConcursal) - _taxes.Unpaid.Where(o => o.Kind == TaxKind.CuotaConcurso).Sum(o => (decimal)o.Total);
        if (residue != 0)
        {
            Post(date, "Redondeo del concurso", "entry.concurso-haircut",
                residue > 0 ? JournalLine.ToDebit(SystemAccounts.DeudaConcursal, residue) : JournalLine.ToDebit(SystemAccounts.QuitaConcursal, -residue),
                residue > 0 ? JournalLine.ToCredit(SystemAccounts.QuitaConcursal, residue) : JournalLine.ToCredit(SystemAccounts.DeudaConcursal, -residue));
        }

        _s.ConcursoActive = true;
        _s.EmbargoSince = null;
        _s.CollectionSince = null;
        _s.LastSeizure = null;
        _s.Events.Add(new LegalEvent(date, LegalEventKind.Concurso));
        return new LegalNotice(LegalNoticeKind.ConcursoOpened, date, toPay, haircut.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Quiebra: liquida toda la cartera, remata el silo y pide al juego rematar bienes antes de condonar el resto.</summary>
    public void DeclareBankruptcy(EconomicDate date, LegalDay day)
    {
        if (_s.BankruptcyPending)
        {
            return;
        }

        _broker.LiquidateEverything(date);
        if (_broker.Cash >= 1)
        {
            PayDebts(Math.Floor(_broker.Cash), SystemAccounts.CuentaComitente, date);
        }

        SeizeSilo(date, day);
        _s.BankruptcyPending = true;
        _s.Events.Add(new LegalEvent(date, LegalEventKind.Bankruptcy));
        day.BankruptcyDeclared = true;
        day.SeizureRequest = DebtInCollection;
        day.Notices.Add(new LegalNotice(LegalNoticeKind.BankruptcyDeclared, date, DebtInCollection));
    }

    /// <summary>Cierra la quiebra: condona lo que no se pudo pagar e inhabilita al jugador.</summary>
    public void FinalizeBankruptcy(EconomicDate date, LegalDay day)
    {
        if (!_s.BankruptcyPending)
        {
            return;
        }

        decimal condoned = 0;
        foreach (TaxObligation o in _taxes.Unpaid.Where(o => o.DueDate <= date || o.Kind is TaxKind.CuotaConcurso or TaxKind.CuotaPrestamo).ToList())
        {
            condoned += _taxes.Close(o.Id, date, SystemAccounts.QuitaConcursal);
        }

        decimal brokerDebt = BrokerDebt;
        if (brokerDebt > 0)
        {
            Post(date, "Condonación de la deuda con el ALyC", "entry.bankruptcy-condoned",
                JournalLine.ToDebit(SystemAccounts.CuentaComitente, brokerDebt),
                JournalLine.ToCredit(SystemAccounts.QuitaConcursal, brokerDebt));
            condoned += brokerDebt;
        }

        foreach (Loan loan in _s.Loans.ToList())
        {
            if (loan.Outstanding > 0)
            {
                Post(date, $"Condonación del préstamo {loan.Id}", "entry.bankruptcy-condoned",
                    JournalLine.ToDebit(SystemAccounts.PrestamosBancarios, loan.Outstanding),
                    JournalLine.ToCredit(SystemAccounts.QuitaConcursal, loan.Outstanding));
                condoned += loan.Outstanding;
            }

            _s.Loans.Remove(loan);
        }

        decimal concursal = _ledger.GetBalance(Entity, SystemAccounts.DeudaConcursal);
        if (concursal > 0)
        {
            Post(date, "Condonación de la deuda concursal", "entry.bankruptcy-condoned",
                JournalLine.ToDebit(SystemAccounts.DeudaConcursal, concursal),
                JournalLine.ToCredit(SystemAccounts.QuitaConcursal, concursal));
        }

        int seasons = (int)Math.Ceiling(Rules.InhabilitationSeasons / Math.Max(0.1m, _difficulty().BankruptcySpeed));
        _s.InhabilitadoUntil = date.FiscalPeriod.AddSeasons(seasons).FirstDay;
        _s.BankruptcyPending = false;
        _s.ConcursoActive = false;
        _s.EmbargoSince = null;
        _s.CollectionSince = null;
        _s.LastSeizure = null;
        _s.Bankruptcies++;
        SetRating("D", date, day.Notices);
        day.Notices.Add(new LegalNotice(LegalNoticeKind.BankruptcyClosed, date, condoned, GameDateText(_s.InhabilitadoUntil.Value)));
    }

    // ───────────────────────────── Préstamos ─────────────────────────────

    public double LoanRate => _centralBank.PolicyRate + _data.Ratings.Level(_s.Rating).Spread + Rules.BankSpread;

    public decimal LoanLimit
    {
        get
        {
            decimal capacity = (AnnualRevenue * Rules.LoanRevenueShare) + (Math.Max(0, NetWorth) * Rules.LoanEquityShare);
            decimal outstanding = _s.Loans.Sum(l => l.Outstanding);
            return Math.Max(0, MoneyRounding.ToGold((capacity * Rules.Factor(_s.Rating)) - outstanding));
        }
    }

    public static long FrenchInstallment(decimal principal, double rate, int periods)
    {
        if (rate <= 0)
        {
            return MoneyRounding.ToGold(principal / periods);
        }

        double factor = rate / (1 - Math.Pow(1 + rate, -periods));
        return MoneyRounding.ToGold(principal * (decimal)factor);
    }

    public LoanQuote QuoteLoan(decimal amount, int term, EconomicDate date)
    {
        double rate = LoanRate;
        decimal limit = LoanLimit;
        long installment = term > 0 ? FrenchInstallment(amount, rate, term) : 0;
        Markets.TradeError error = IsInhibited ? Markets.TradeError.Inhibited
            : !Rules.LoanTerms.Contains(term) ? Markets.TradeError.InvalidTerm
            : amount < Rules.LoanMinimum ? Markets.TradeError.BelowMinimum
            : amount > limit ? Markets.TradeError.AboveLimit
            : Markets.TradeError.None;
        return new LoanQuote(amount, term, rate, installment, limit, error);
    }

    /// <summary>Otorga un préstamo: el oro va a la Caja (el juego debe sumarlo al jugador).</summary>
    public LoanQuote TakeLoan(decimal amount, int term, EconomicDate date, List<LegalNotice> notices)
    {
        LoanQuote q = QuoteLoan(MoneyRounding.ToGold(amount), term, date);
        if (q.Error != Markets.TradeError.None)
        {
            return q;
        }

        Post(date, $"Préstamo del Banco de Zuzu a {term} estaciones", "entry.loan-taken",
            JournalLine.ToDebit(SystemAccounts.Caja, q.Amount),
            JournalLine.ToCredit(SystemAccounts.PrestamosBancarios, q.Amount));
        _s.Loans.Add(new Loan
        {
            Id = _s.NextLoanId++,
            Principal = q.Amount,
            Rate = q.Rate,
            TermSeasons = term,
            TakenOn = date,
            Outstanding = q.Amount,
            Installment = q.Installment,
        });
        notices.Add(new LegalNotice(LegalNoticeKind.LoanTaken, date, q.Amount));
        return q;
    }

    /// <summary>Al empezar cada estación: emite la cuota de cada préstamo (interés + amortización) como boleta del banco.</summary>
    public void IssueLoanInstallments(EconomicDate date, List<TaxNotice> taxNotices)
    {
        foreach (Loan loan in _s.Loans.ToList())
        {
            if (loan.InstallmentsIssued >= loan.TermSeasons || loan.TakenOn.FiscalPeriod == date.FiscalPeriod)
            {
                continue;
            }

            int k = loan.InstallmentsIssued + 1;
            // Capital, cuota e intereses son oro entero: la amortización también.
            long interest = MoneyRounding.ToGold(loan.Outstanding * (decimal)loan.Rate);
            decimal amortization = k == loan.TermSeasons ? loan.Outstanding : Math.Max(0, Math.Min(loan.Outstanding, loan.Installment - interest));
            long total = interest + MoneyRounding.ToGold(amortization);
            var lines = new List<JournalLine>();
            if (interest > 0)
            {
                lines.Add(JournalLine.ToDebit(SystemAccounts.InteresesBancarios, interest));
            }

            if (amortization > 0)
            {
                lines.Add(JournalLine.ToDebit(SystemAccounts.PrestamosBancarios, amortization));
            }

            lines.Add(JournalLine.ToCredit(SystemAccounts.CuotasAPagarBanco, interest + amortization));
            Post(date, $"Cuota {k} del préstamo {loan.Id}", "entry.loan-installment", lines.ToArray());
            EconomicDate due = new EconomicDate(date.Year, date.Season, _taxes.Rules.DueDay).NextBusinessDayOrSelf();
            _taxes.CreateExternal(TaxKind.CuotaPrestamo, date.FiscalPeriod, total, date, due <= date ? date.AddDays(7) : due, taxNotices);

            loan.Outstanding -= amortization;
            loan.InstallmentsIssued = k;
            if (loan.InstallmentsIssued >= loan.TermSeasons)
            {
                _s.Loans.Remove(loan);
            }
        }
    }

    // ───────────────────────────── Calificación ─────────────────────────────

    /// <summary>Recalcula la calificación del jugador (cada estación).</summary>
    public void UpdateRating(EconomicDate date, List<LegalNotice> notices)
    {
        _s.Events.RemoveAll(e => e.Date.DaysUntil(date) > EconomicDate.DaysPerYear * 2);
        if (_s.BankruptcyPending || IsInhabilitado(date))
        {
            SetRating("D", date, notices);
            return;
        }

        bool Recent(LegalEvent e, int days) => e.Date.DaysUntil(date) <= days;
        int year = EconomicDate.DaysPerYear;
        int overdue = _s.Events.Count(e => e.Kind == LegalEventKind.Overdue && Recent(e, year));
        int intimations = _s.Events.Count(e => e.Kind == LegalEventKind.Intimation && Recent(e, year));
        int embargoes = _s.Events.Count(e => e.Kind == LegalEventKind.Embargo && Recent(e, year));
        int concursos = _s.Events.Count(e => e.Kind == LegalEventKind.Concurso && Recent(e, year * 2));
        int bankruptcies = _s.Events.Count(e => e.Kind == LegalEventKind.Bankruptcy && Recent(e, year * 2));

        double revenue = Math.Max(1000, (double)AnnualRevenue);
        double debt = (double)(_ledger.GetBalance(Entity, SystemAccounts.PrestamosBancarios) + _ledger.GetBalance(Entity, SystemAccounts.CaucionTomadora));
        double score = 55
            + Math.Clamp((double)NetWorth / revenue * 10, -15, 20)
            - Math.Clamp(debt / revenue * 20, 0, 25)
            - Math.Min(20, 4 * overdue)
            - Math.Min(20, 8 * intimations)
            - (20 * embargoes) - (25 * concursos) - (40 * bankruptcies)
            - (IsEmbargoActive ? 15 : 0) - (_s.ConcursoActive ? 15 : 0);
        _s.Score = score;
        string rating = score >= 85 ? "AAA" : score >= 75 ? "AA" : score >= 65 ? "A" : score >= 55 ? "BBB"
            : score >= 45 ? "BB" : score >= 35 ? "B" : score >= 20 ? "CCC" : "D";
        SetRating(rating, date, notices);
    }

    private void SetRating(string rating, EconomicDate date, List<LegalNotice> notices)
    {
        if (rating != _s.Rating)
        {
            notices.Add(new LegalNotice(LegalNoticeKind.RatingChanged, date, 0, rating));
            _s.Rating = rating;
        }
    }

    // ───────────────────────────── Internos ─────────────────────────────

    private void SeizeSilo(EconomicDate date, LegalDay day)
    {
        decimal debt = DebtInCollection;
        if (debt < 1 || !HasEmbargo(Embargo_Goods) && !_s.BankruptcyPending && !day.BankruptcyDeclared)
        {
            return;
        }

        decimal value = 0;
        foreach (SiloItem item in _broker.Silo.OrderByDescending(i => i.BasePrice * (1 + _broker.DerivativesRules.QualityPremium(i.Quality))).ToList())
        {
            if (value >= debt)
            {
                break;
            }

            decimal unit = item.BasePrice * (decimal)(1 + _broker.DerivativesRules.QualityPremium(item.Quality)) * Rules.SeizureValueRate;
            int take = unit <= 0 ? item.Stack : (int)Math.Min(item.Stack, Math.Ceiling((debt - value) / unit));
            value += unit * take;
            _broker.RemoveFromSilo(item, take);
        }

        ApplySeizure(MoneyRounding.Round(value), date, day);
    }

    private int Threshold(int days) => (int)Math.Ceiling(days / Math.Max(0.1m, _difficulty().BankruptcySpeed));

    private void Post(EconomicDate date, string fallback, string key, params JournalLine[] lines) =>
        _ledger.Post(Entity, date, new EntryText(fallback, key), EntrySources.Ajuste, lines);

    private static string GameDateText(EconomicDate date) =>
        string.Create(CultureInfo.InvariantCulture, $"{date.Year}:{(int)date.Season}:{date.Day}");
}
