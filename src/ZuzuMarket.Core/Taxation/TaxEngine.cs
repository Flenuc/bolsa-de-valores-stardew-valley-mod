using System.Globalization;
using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Ledger;
using ZuzuMarket.Core.Money;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Taxation;

public enum SaleChannel
{
    /// <summary>Cajón de envíos: se cobra a la noche (queda en Deudores por ventas hasta entonces).</summary>
    Shipping,

    /// <summary>Venta directa en una tienda: se cobra en el momento.</summary>
    Shop,
}

/// <summary>Objeto vendido: id y categoría del juego, unidades e importe cobrado (IVA incluido).</summary>
public sealed record SoldItem(string ItemId, int Category, int Units, long Gross);

/// <summary>
/// Motor tributario (§9 del diseño): registra ventas y compras con su IVA, emite las boletas de cada período,
/// liquida Ganancias anual, recategoriza el Monotributo y gestiona la mora.
/// Todo pasa por el libro contable de la entidad <see cref="LedgerEntities.Jugador"/>.
/// </summary>
public sealed class TaxEngine
{
    private const string Entity = LedgerEntities.Jugador;

    private readonly GameData _data;
    private readonly TaxState _s;
    private readonly GeneralLedger _ledger;
    private readonly DifficultyDefinition _difficulty;

    public TaxEngine(GameData data, TaxState state, GeneralLedger ledger, DifficultyDefinition difficulty)
    {
        _data = data;
        _s = state;
        _ledger = ledger;
        _difficulty = difficulty;
    }

    public TaxRules Rules => _data.Taxes;

    public TaxState State => _s;

    public bool Started => _s.Started;

    public TaxRegime Regime => _s.Regime;

    public string Category => _s.Category;

    private decimal Burden => _difficulty.TaxBurden;

    public IEnumerable<TaxObligation> Unpaid => _s.Obligations.Where(o => !o.IsPaid);

    public TaxObligation? Find(long id) => _s.Obligations.FirstOrDefault(o => o.Id == id);

    public string ActivityFor(string itemId, int category) => Rules.ActivityFor(_data.FindCommodityByItemId(itemId), category);

    /// <summary>Boletas impagas que ya vencieron o vencen hoy, de la más vieja a la más nueva.</summary>
    public IReadOnlyList<TaxObligation> DueOn(EconomicDate date) =>
        Unpaid.Where(o => o.DueDate <= date).OrderBy(o => o.DueDate).ThenBy(o => o.Id).ToList();

    /// <summary>Alta como monotributista (categoría A) y boletas del período en curso. Sin período de gracia.</summary>
    public IReadOnlyList<TaxNotice> Start(EconomicDate date, FarmSnapshot farm, decimal ipc)
    {
        var notices = new List<TaxNotice>();
        if (_s.Started)
        {
            return notices;
        }

        _s.Started = true;
        _s.RegisteredOn = date;
        _s.Regime = TaxRegime.Monotributo;
        _s.Category = Rules.Monotributo.Categories.Count > 0 ? Rules.Monotributo.Categories[0].Id : "";
        _s.Current = new PeriodAccumulator { Period = date.FiscalPeriod, Regime = _s.Regime };
        _s.Year = new YearAccumulator { Year = date.Year };
        notices.Add(new TaxNotice(TaxNoticeKind.Registered, date, Detail: _s.Category));
        IssueForCurrentPeriod(date, farm, ipc, notices);
        return notices;
    }

    // ───────────────────────────── Ventas y compras ─────────────────────────────

    /// <summary>Registra ventas agrupadas por actividad (un asiento por actividad).</summary>
    public void RecordSales(EconomicDate date, IEnumerable<SoldItem> items, SaleChannel channel)
    {
        foreach (IGrouping<string, SoldItem> group in items.Where(i => i.Gross > 0).GroupBy(i => ActivityFor(i.ItemId, i.Category)))
        {
            RecordSale(date, group.Key, group.Sum(i => i.Gross), channel);
        }
    }

    /// <summary>Registra una venta (importe con IVA incluido si es Responsable Inscripto).</summary>
    public JournalEntry? RecordSale(EconomicDate date, string activity, decimal gross, SaleChannel channel, string? debitAccount = null, EntryText? description = null)
    {
        if (gross <= 0)
        {
            return null;
        }

        bool ri = _s.Regime == TaxRegime.ResponsableInscripto;
        decimal iva = 0;
        if (ri)
        {
            decimal rate = Rules.Iva.For(activity) * Burden;
            iva = MoneyRounding.Round(gross * rate / (1 + rate));
        }

        decimal net = gross - iva;
        var lines = new List<JournalLine>
        {
            JournalLine.ToDebit(debitAccount ?? (channel == SaleChannel.Shipping ? SystemAccounts.DeudoresPorVentas : SystemAccounts.Caja), gross),
            JournalLine.ToCredit(SystemAccounts.Ventas, net),
        };
        if (iva > 0)
        {
            lines.Add(JournalLine.ToCredit(SystemAccounts.IvaDebitoFiscal, iva));
        }

        string key = channel == SaleChannel.Shipping ? "entry.sale-shipping" : "entry.sale-shop";
        EntryText text = description ?? new EntryText($"Venta ({activity})", key, Args(("activity", activity)));
        JournalEntry entry = _ledger.Post(Entity, date, text, EntrySources.Venta, lines.ToArray());

        _s.Current.NetSalesByActivity.TryGetValue(activity, out decimal previous);
        _s.Current.NetSalesByActivity[activity] = previous + net;
        if (ri)
        {
            _s.Year.HadResponsableInscripto = true;
            _s.Year.NetSales += net;
        }

        return entry;
    }

    /// <summary>Registra una compra en una tienda (con factura: da crédito fiscal si es Responsable Inscripto).</summary>
    public JournalEntry? RecordPurchase(EconomicDate date, decimal gross, string? creditAccount = null)
    {
        if (gross <= 0)
        {
            return null;
        }

        bool ri = _s.Regime == TaxRegime.ResponsableInscripto;
        decimal iva = 0;
        if (ri)
        {
            decimal rate = Rules.Iva.PurchaseRate * Burden;
            iva = MoneyRounding.Round(gross * rate / (1 + rate));
        }

        decimal net = gross - iva;
        var lines = new List<JournalLine> { JournalLine.ToDebit(SystemAccounts.ComprasEnTiendas, net) };
        if (iva > 0)
        {
            lines.Add(JournalLine.ToDebit(SystemAccounts.IvaCreditoFiscal, iva));
        }

        lines.Add(JournalLine.ToCredit(creditAccount ?? SystemAccounts.Caja, gross));
        JournalEntry entry = _ledger.Post(Entity, date, new EntryText("Compra en tienda", "entry.purchase"), EntrySources.Compra, lines.ToArray());

        _s.Current.NetPurchases += net;
        if (ri)
        {
            _s.Year.HadResponsableInscripto = true;
            _s.Year.NetPurchases += net;
        }

        return entry;
    }

    // ───────────────────────────── Cierre de períodos ─────────────────────────────

    /// <summary>
    /// Al empezar un período: declara el anterior (IIBB, SyH, IVA), recategoriza o excluye del Monotributo,
    /// liquida Ganancias si empezó un año y emite las boletas del período nuevo. No hace nada si ya se procesó.
    /// </summary>
    public IReadOnlyList<TaxNotice> OnPeriodStart(EconomicDate date, FarmSnapshot farm, decimal ipc)
    {
        if (!_s.Started)
        {
            return Start(date, farm, ipc);
        }

        var notices = new List<TaxNotice>();
        if (_s.Current.Period == date.FiscalPeriod)
        {
            return notices;
        }

        PeriodAccumulator previous = _s.Current;
        DeclareMonthly(previous, date, ipc, notices);
        _s.RevenueHistory.Add(new PeriodRevenue(previous.Period, previous.NetSales));

        if (_s.Regime == TaxRegime.Monotributo)
        {
            CheckMonotributo(previous, date, ipc, notices);
        }

        if (date.Year > _s.Year.Year)
        {
            DeclareGanancias(date, ipc, notices);
            _s.Year = new YearAccumulator { Year = date.Year };
        }

        _s.Current = new PeriodAccumulator { Period = date.FiscalPeriod, Regime = _s.Regime };
        IssueForCurrentPeriod(date, farm, ipc, notices);
        return notices;
    }

    /// <summary>Cada día: vencimientos, intereses resarcitorios, intimaciones y multas.</summary>
    public IReadOnlyList<TaxNotice> OnDay(EconomicDate date)
    {
        var notices = new List<TaxNotice>();
        MoraRules mora = Rules.Mora;
        foreach (TaxObligation o in Unpaid.ToList())
        {
            if (date <= o.DueDate)
            {
                continue;
            }

            if (o.Status == ObligationStatus.Pending)
            {
                o.Status = ObligationStatus.Overdue;
                notices.Add(new TaxNotice(TaxNoticeKind.Overdue, date, o.Id));
            }

            EconomicDate from = o.LastAccrual ?? o.DueDate;
            int days = from.DaysUntil(date);
            if (days > 0)
            {
                o.Interest += o.Principal * mora.DailyInterest * _difficulty.LateInterest * days;
                o.LastAccrual = date;
            }

            if (o.Status == ObligationStatus.Overdue && o.DueDate.DaysUntil(date) >= mora.IntimationAfterDays)
            {
                o.Status = ObligationStatus.Intimated;
                o.IntimationDeadline = date.AddDays(mora.IntimationDeadlineDays);
                notices.Add(new TaxNotice(TaxNoticeKind.Intimated, date, o.Id));
            }
            else if (o.Status == ObligationStatus.Intimated && o.IntimationDeadline is EconomicDate deadline && date > deadline)
            {
                o.Status = ObligationStatus.InCollection;
                o.Fine = MoneyRounding.ToGold(o.Principal * mora.FineRate);
                if (o.Fine > 0)
                {
                    _ledger.Post(
                        Entity,
                        date,
                        new EntryText($"Multa por omisión, boleta {o.Id}", "entry.tax-fine", Args(("id", o.Id.ToString(CultureInfo.InvariantCulture)))),
                        EntrySources.Impuesto,
                        JournalLine.ToDebit(SystemAccounts.InteresesYMultasFiscales, o.Fine),
                        JournalLine.ToCredit(PayableAccount(o.Jurisdiction), o.Fine));
                }

                notices.Add(new TaxNotice(TaxNoticeKind.InCollection, date, o.Id));
            }
        }

        return notices;
    }

    /// <summary>
    /// Paga una boleta completa (capital + intereses + multa) contra la Caja. El juego debe descontar el oro devuelto.
    /// Devuelve <c>null</c> si la boleta no existe o ya estaba paga.
    /// </summary>
    public long? Pay(long obligationId, EconomicDate date)
    {
        TaxObligation? o = Find(obligationId);
        if (o is null || o.IsPaid)
        {
            return null;
        }

        long total = o.Total;
        long paid = PayFrom(obligationId, total, date, SystemAccounts.Caja);
        return paid;
    }

    /// <summary>Cambia el régimen inmediatamente (comando de debug).</summary>
    public TaxNotice SetRegime(TaxRegime regime, EconomicDate date)
    {
        _s.Regime = regime;
        _s.Current.Regime = regime;
        return new TaxNotice(TaxNoticeKind.RegimeChanged, date, Detail: regime.ToString());
    }

    public static string PayableAccount(Jurisdiction jurisdiction) => jurisdiction switch
    {
        Jurisdiction.Arpv => SystemAccounts.ImpuestosAPagarArpv,
        Jurisdiction.Municipalidad => SystemAccounts.TasasAPagarMunicipalidad,
        Jurisdiction.Banco => SystemAccounts.CuotasAPagarBanco,
        Jurisdiction.Concurso => SystemAccounts.DeudaConcursal,
        _ => SystemAccounts.ImpuestosAPagarArf,
    };

    /// <summary>Boletas exigibles hoy: vencidas o que vencen hoy, impagas.</summary>
    public IReadOnlyList<TaxObligation> InCollection => Unpaid.Where(o => o.Status == ObligationStatus.InCollection).ToList();

    /// <summary>
    /// Paga hasta <paramref name="maxAmount"/> de una boleta desde <paramref name="sourceAccount"/> (Caja, cuenta comitente,
    /// deudores por ventas embargados, remate…). Primero asienta los intereses devengados; aplica a multa y después a capital.
    /// Devuelve lo pagado.
    /// </summary>
    public long PayFrom(long obligationId, decimal maxAmount, EconomicDate date, string sourceAccount)
    {
        TaxObligation? o = Find(obligationId);
        if (o is null || o.IsPaid || maxAmount < 1)
        {
            return 0;
        }

        string payable = PayableAccount(o.Jurisdiction);
        var args = Args(("id", o.Id.ToString(CultureInfo.InvariantCulture)), ("tax", o.Kind.ToString()));
        long interest = MoneyRounding.ToGold(o.Interest);
        if (interest > 0)
        {
            _ledger.Post(Entity, date, new EntryText($"Intereses resarcitorios, boleta {o.Id}", "entry.tax-interest", args), EntrySources.Impuesto,
                JournalLine.ToDebit(SystemAccounts.InteresesYMultasFiscales, interest),
                JournalLine.ToCredit(payable, interest));
            o.Principal += interest;
        }

        o.Interest = 0;
        long pay = Math.Min((long)Math.Floor(maxAmount), o.Principal + o.Fine);
        if (pay <= 0)
        {
            return 0;
        }

        _ledger.Post(Entity, date, new EntryText($"Pago de {o.Kind}, boleta {o.Id}", "entry.tax-paid", args), EntrySources.Pago,
            JournalLine.ToDebit(payable, pay),
            JournalLine.ToCredit(sourceAccount, pay));

        long fromFine = Math.Min(o.Fine, pay);
        o.Fine -= fromFine;
        o.Principal -= pay - fromFine;
        o.PaidAmount += pay;
        if (o.Principal + o.Fine <= 0)
        {
            o.Status = ObligationStatus.Paid;
            o.PaidOn = date;
        }

        return pay;
    }

    /// <summary>Cierra una boleta sin pagarla: su saldo pasa a <paramref name="counterAccount"/> (deuda concursal o quita).</summary>
    public long Close(long obligationId, EconomicDate date, string counterAccount)
    {
        TaxObligation? o = Find(obligationId);
        if (o is null || o.IsPaid)
        {
            return 0;
        }

        string payable = PayableAccount(o.Jurisdiction);
        long interest = MoneyRounding.ToGold(o.Interest);
        if (interest > 0)
        {
            _ledger.Post(Entity, date, new EntryText($"Intereses resarcitorios, boleta {o.Id}", "entry.tax-interest", Args(("id", o.Id.ToString(CultureInfo.InvariantCulture)))), EntrySources.Impuesto,
                JournalLine.ToDebit(SystemAccounts.InteresesYMultasFiscales, interest),
                JournalLine.ToCredit(payable, interest));
        }

        long total = o.Principal + interest + o.Fine;
        if (total > 0)
        {
            _ledger.Post(Entity, date, new EntryText($"Cierre de la boleta {o.Id}", "entry.obligation-closed", Args(("id", o.Id.ToString(CultureInfo.InvariantCulture)))), EntrySources.Ajuste,
                JournalLine.ToDebit(payable, total),
                JournalLine.ToCredit(counterAccount, total));
        }

        o.Interest = interest;
        o.Status = ObligationStatus.Restructured;
        o.PaidOn = date;
        return total;
    }

    /// <summary>Crea una boleta de otro acreedor (banco, concurso…) ya asentada por quien la crea.</summary>
    internal TaxObligation CreateExternal(TaxKind kind, FiscalPeriod period, long principal, EconomicDate date, EconomicDate due, List<TaxNotice> notices)
    {
        TaxObligation o = CreateObligation(kind, period, principal, date, Rules.DueDay, notices);
        o.DueDate = due;
        return o;
    }

    // ───────────────────────────── Internos ─────────────────────────────

    private void DeclareMonthly(PeriodAccumulator previous, EconomicDate date, decimal ipc, List<TaxNotice> notices)
    {
        decimal iibb = previous.NetSalesByActivity.Sum(kv => kv.Value * Rules.IngresosBrutos.For(kv.Key)) * Burden;
        if (Issue(TaxKind.IngresosBrutos, previous.Period, iibb, date, Rules.DueDay, SystemAccounts.IngresosBrutos, notices) is { } iibbObligation)
        {
            _s.Year.DeductibleTaxes += iibbObligation.Principal;
        }

        SeguridadHigieneRules syh = Rules.SeguridadHigiene;
        decimal seguridad = Math.Max(previous.NetSales * syh.Rate * Burden, syh.Minimum * ipc * Burden);
        if (Issue(TaxKind.SeguridadHigiene, previous.Period, seguridad, date, Rules.DueDay, SystemAccounts.SeguridadHigiene, notices) is { } syhObligation)
        {
            _s.Year.DeductibleTaxes += syhObligation.Principal;
        }

        SettleIva(previous.Period, date, notices);
    }

    /// <summary>Compensa débito y crédito fiscal. El saldo a favor queda en la cuenta IVA Crédito Fiscal.</summary>
    private void SettleIva(FiscalPeriod period, EconomicDate date, List<TaxNotice> notices)
    {
        decimal debit = _ledger.GetBalance(Entity, SystemAccounts.IvaDebitoFiscal);
        if (debit <= 0)
        {
            return;
        }

        decimal credit = Math.Max(0, _ledger.GetBalance(Entity, SystemAccounts.IvaCreditoFiscal));
        var text = new EntryText($"Liquidación de IVA {period}", "entry.iva-settlement", PeriodArgs(period));
        if (credit >= debit)
        {
            _ledger.Post(Entity, date, text, EntrySources.Impuesto,
                JournalLine.ToDebit(SystemAccounts.IvaDebitoFiscal, debit),
                JournalLine.ToCredit(SystemAccounts.IvaCreditoFiscal, debit));
            return;
        }

        long toPay = MoneyRounding.ToGold(debit - credit, out decimal residue);
        var lines = new List<JournalLine> { JournalLine.ToDebit(SystemAccounts.IvaDebitoFiscal, debit) };
        if (credit > 0)
        {
            lines.Add(JournalLine.ToCredit(SystemAccounts.IvaCreditoFiscal, credit));
        }

        if (toPay > 0)
        {
            lines.Add(JournalLine.ToCredit(SystemAccounts.ImpuestosAPagarArf, toPay));
        }

        if (residue > 0)
        {
            lines.Add(JournalLine.ToCredit(SystemAccounts.DiferenciasDeRedondeo, residue));
        }
        else if (residue < 0)
        {
            lines.Add(JournalLine.ToDebit(SystemAccounts.DiferenciasDeRedondeo, -residue));
        }

        _ledger.Post(Entity, date, text, EntrySources.Impuesto, lines.ToArray());
        if (toPay > 0)
        {
            CreateObligation(TaxKind.Iva, period, toPay, date, Rules.DueDay, notices);
        }
    }

    private void CheckMonotributo(PeriodAccumulator previous, EconomicDate date, decimal ipc, List<TaxNotice> notices)
    {
        List<MonotributoCategory> categories = Rules.Monotributo.Categories;
        if (categories.Count == 0)
        {
            return;
        }

        List<PeriodRevenue> last = _s.RevenueHistory.Skip(Math.Max(0, _s.RevenueHistory.Count - 4)).ToList();
        decimal actual = last.Sum(r => r.NetSales);
        if (actual > categories[^1].AnnualCap * ipc)
        {
            Exclude(date, notices);
            return;
        }

        if (!Rules.Monotributo.RecategorizationAfter.Contains(previous.Period.Season))
        {
            return;
        }

        decimal annualized = last.Count == 0 ? 0 : actual * 4 / last.Count;
        MonotributoCategory? category = categories.FirstOrDefault(c => annualized <= c.AnnualCap * ipc);
        if (category is null)
        {
            Exclude(date, notices);
            return;
        }

        if (category.Id != _s.Category)
        {
            _s.Category = category.Id;
            notices.Add(new TaxNotice(TaxNoticeKind.Recategorized, date, Detail: category.Id));
        }
    }

    private void Exclude(EconomicDate date, List<TaxNotice> notices)
    {
        _s.Regime = TaxRegime.ResponsableInscripto;
        notices.Add(new TaxNotice(TaxNoticeKind.Excluded, date));
    }

    private void DeclareGanancias(EconomicDate date, decimal ipc, List<TaxNotice> notices)
    {
        YearAccumulator year = _s.Year;
        _s.AdvancesRemaining = 0;
        if (!year.HadResponsableInscripto)
        {
            return;
        }

        GananciasRules rules = Rules.Ganancias;
        decimal taxable = year.NetSales - year.NetPurchases - year.DeductibleTaxes - (rules.NonTaxableMinimum * ipc);
        long tax = taxable > 0 ? MoneyRounding.ToGold(rules.ProgressiveTax(taxable, ipc) * Burden) : 0;
        if (tax <= 0)
        {
            return;
        }

        decimal advances = Math.Max(0, _ledger.GetBalance(Entity, SystemAccounts.AnticiposGanancias));
        decimal fromAdvances = Math.Min(advances, tax);
        long toPay = tax - MoneyRounding.ToGold(fromAdvances);
        var period = new FiscalPeriod(year.Year, Season.Winter);
        var lines = new List<JournalLine> { JournalLine.ToDebit(SystemAccounts.Ganancias, tax) };
        if (fromAdvances > 0)
        {
            lines.Add(JournalLine.ToCredit(SystemAccounts.AnticiposGanancias, tax - toPay));
        }

        if (toPay > 0)
        {
            lines.Add(JournalLine.ToCredit(SystemAccounts.ImpuestosAPagarArf, toPay));
        }

        _ledger.Post(
            Entity,
            date,
            new EntryText($"Impuesto a las Ganancias, año {year.Year}", "entry.ganancias", Args(("year", year.Year.ToString(CultureInfo.InvariantCulture)))),
            EntrySources.Impuesto,
            lines.ToArray());

        if (toPay > 0)
        {
            CreateObligation(TaxKind.Ganancias, period, toPay, date, Rules.GananciasDueDay, notices);
        }

        _s.AdvancesRemaining = rules.Advances;
        _s.AdvanceAmount = tax * rules.AdvanceFraction;
    }

    /// <summary>Cuota del Monotributo, ABL y anticipos de Ganancias del período que empieza.</summary>
    private void IssueForCurrentPeriod(EconomicDate date, FarmSnapshot farm, decimal ipc, List<TaxNotice> notices)
    {
        FiscalPeriod period = date.FiscalPeriod;
        if (_s.Regime == TaxRegime.Monotributo
            && Rules.Monotributo.Categories.FirstOrDefault(c => c.Id == _s.Category) is { } category)
        {
            Issue(TaxKind.Monotributo, period, category.Fee * ipc * Burden, date, Rules.DueDay, SystemAccounts.Monotributo, notices);
        }

        if (Issue(TaxKind.Abl, period, Rules.Abl.For(farm) * ipc * Burden, date, Rules.DueDay, SystemAccounts.Abl, notices) is { } abl)
        {
            _s.Year.DeductibleTaxes += abl.Principal;
        }

        // Los anticipos van en las estaciones siguientes a la declaración anual (que se presenta en primavera).
        if (_s.AdvancesRemaining > 0 && date.Season != Season.Spring)
        {
            long advance = MoneyRounding.ToGold(_s.AdvanceAmount);
            if (advance > 0)
            {
                _ledger.Post(
                    Entity,
                    date,
                    new EntryText($"Anticipo de Ganancias {period}", "entry.ganancias-advance", PeriodArgs(period)),
                    EntrySources.Impuesto,
                    JournalLine.ToDebit(SystemAccounts.AnticiposGanancias, advance),
                    JournalLine.ToCredit(SystemAccounts.ImpuestosAPagarArf, advance));
                CreateObligation(TaxKind.GananciasAnticipo, period, advance, date, Rules.DueDay, notices);
            }

            _s.AdvancesRemaining--;
        }
    }

    /// <summary>Emite una boleta: asienta el gasto contra el pasivo de la jurisdicción.</summary>
    private TaxObligation? Issue(TaxKind kind, FiscalPeriod period, decimal amount, EconomicDate date, int dueDay, string expenseAccount, List<TaxNotice> notices)
    {
        long principal = MoneyRounding.ToGold(amount);
        if (principal <= 0)
        {
            return null;
        }

        _ledger.Post(
            Entity,
            date,
            new EntryText($"{kind} {period}", "entry.tax-issued", Args(("tax", kind.ToString()), ("season", period.Season.ToString()), ("year", period.Year.ToString(CultureInfo.InvariantCulture)))),
            EntrySources.Impuesto,
            JournalLine.ToDebit(expenseAccount, principal),
            JournalLine.ToCredit(PayableAccount(kind.Jurisdiction()), principal));
        return CreateObligation(kind, period, principal, date, dueDay, notices);
    }

    private TaxObligation CreateObligation(TaxKind kind, FiscalPeriod period, long principal, EconomicDate date, int dueDay, List<TaxNotice> notices)
    {
        EconomicDate due = new EconomicDate(date.Year, date.Season, Math.Clamp(dueDay, 1, EconomicDate.DaysPerSeason)).NextBusinessDayOrSelf();
        if (due <= date)
        {
            // Alta a mitad de período: se dan 7 días.
            due = date.AddDays(7).NextBusinessDayOrSelf();
        }

        var obligation = new TaxObligation
        {
            Id = _s.NextObligationId++,
            Kind = kind,
            Period = period,
            Principal = principal,
            IssuedOn = date,
            DueDate = due,
        };
        _s.Obligations.Add(obligation);
        notices.Add(new TaxNotice(TaxNoticeKind.Issued, date, obligation.Id));
        return obligation;
    }

    private static IReadOnlyDictionary<string, string> Args(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    private static IReadOnlyDictionary<string, string> PeriodArgs(FiscalPeriod period) =>
        Args(("season", period.Season.ToString()), ("year", period.Year.ToString(CultureInfo.InvariantCulture)));
}
