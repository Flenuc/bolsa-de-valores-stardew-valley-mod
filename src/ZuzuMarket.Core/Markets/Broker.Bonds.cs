using System.Globalization;
using ZuzuMarket.Core.Ledger;
using ZuzuMarket.Core.Money;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Markets;

/// <summary>Bonos soberanos y obligaciones negociables: emisión, valuación, cupones, amortizaciones y canje.</summary>
public sealed partial class Broker
{
    public BondSeries? FindBond(string id) =>
        _s.Bonds.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Series vigentes: soberanas (<paramref name="sovereign"/> = true) u obligaciones negociables.</summary>
    public IReadOnlyList<BondSeries> ActiveBonds(bool sovereign) =>
        _s.Bonds.Where(b => b.IsSovereign == sovereign).OrderBy(b => b.Maturity).ThenBy(b => b.Id).ToList();

    public BondContext Context(string issuer) => new(
        ReferenceRate,
        _inflation.PeriodRate,
        _inflation.Index,
        days => Yield(days, issuer) + Rules.BondSpread);

    /// <summary>Contexto de valuación: los bonos en oro gotoro se descuentan a la tasa de Gotoro, sin inflación local.</summary>
    public BondContext ContextFor(BondSeries bond) => bond.IsGotoro
        ? new BondContext(GotoroRate, 0, 1, days => GotoroRate + Rules.Premium(days) + _credit.Spread(bond.Issuer) + Rules.BondSpread)
        : Context(bond.Issuer);

    private double GotoroRate => Fx?.Rules.GotoroRate ?? 0.008;

    public bool IsDefaulted(BondSeries bond) => _credit.IsDefaulted(bond.Issuer);

    /// <summary>Precio por lámina de VN 100 en la moneda del bono (en default: valor de recupero).</summary>
    public double BondNativePrice(BondSeries bond, EconomicDate date)
    {
        BondContext ctx = ContextFor(bond);
        return IsDefaulted(bond) ? BondMath.DefaultedPrice(bond, ctx, _credit.Recovery(bond.Issuer)) : BondMath.Price(bond, date, ctx);
    }

    /// <summary>Precio por lámina de VN 100 en oro local (los bonos en oro gotoro, al MEP).</summary>
    public double BondPrice(BondSeries bond, EconomicDate date) =>
        BondNativePrice(bond, date) * (bond.IsGotoro ? Fx?.Mep ?? 1 : 1);

    /// <summary>TIR mensual al precio vigente (en la moneda del bono).</summary>
    public double BondYield(BondSeries bond, EconomicDate date) =>
        BondMath.Yield(bond, date, ContextFor(bond), BondNativePrice(bond, date));

    public double BondDuration(BondSeries bond, EconomicDate date) => BondMath.Duration(bond, date, ContextFor(bond));

    public IReadOnlyList<BondCashFlow> BondFlows(BondSeries bond) => BondMath.RemainingFlows(bond, ContextFor(bond));

    public string Rating(string issuer) => _credit.Issuers.TryGetValue(issuer, out IssuerState? s) ? s.Rating : "-";

    /// <summary>Emite una serie de cada plantilla (salvo emisores en default). El cupón fijo se fija con la curva.</summary>
    public void IssueBonds(EconomicDate date)
    {
        foreach (BondTemplate t in _data.BondTemplates)
        {
            string id = $"{t.Prefix}{date.Year}";
            if (FindBond(id) is not null || _credit.IsDefaulted(t.Issuer) || !_credit.Issuers.ContainsKey(t.Issuer))
            {
                continue;
            }

            bool gotoro = t.Currency.Equals("GTD", StringComparison.OrdinalIgnoreCase);
            double fixedYield = gotoro
                ? GotoroRate + Rules.Premium(t.TenorSeasons * EconomicDate.DaysPerSeason) + _credit.Spread(t.Issuer)
                : Yield(t.TenorSeasons * EconomicDate.DaysPerSeason, t.Issuer);
            double coupon = t.Type switch
            {
                BondType.Fixed => Math.Round(fixedYield / 0.0025) * 0.0025,
                _ => t.Coupon,
            };
            _s.Bonds.Add(new BondSeries
            {
                Id = id,
                Prefix = t.Prefix,
                Issuer = t.Issuer,
                Type = t.Type,
                IssueDate = date.FiscalPeriod.FirstDay,
                TenorSeasons = t.TenorSeasons,
                CouponRate = coupon,
                AmortizationInstallments = Math.Clamp(t.AmortizationInstallments, 1, t.TenorSeasons),
                CerBase = _inflation.Index,
                Currency = t.Currency,
            });
        }
    }

    /// <summary>Cobra los cupones y amortizaciones que vencen hasta hoy (los emisores en default no pagan).</summary>
    public List<MarketNotice> ProcessBondPayments(EconomicDate date)
    {
        var notices = new List<MarketNotice>();
        foreach (BondSeries bond in _s.Bonds.ToList())
        {
            while (bond.NextPayment is EconomicDate due && due <= date && !IsDefaulted(bond))
            {
                int k = bond.PaymentsMade + 1;
                BondCashFlow flow = BondMath.RemainingFlows(bond, ContextFor(bond))[0];
                if (bond.IssuedUnits > 0 && PayIssuer is not null && !PayIssuer(bond, flow, date))
                {
                    break;
                }

                if (_s.Broker.Bonds.TryGetValue(bond.Id, out Holding? holding))
                {
                    notices.Add(Collect(bond, holding, flow, date));
                }

                bond.PaymentsMade = k;
                bond.Outstanding = Math.Max(0, bond.Outstanding - bond.AmortizationFraction(k));
            }

            if (bond.PaymentsMade >= bond.TenorSeasons)
            {
                if (_s.Broker.Bonds.Remove(bond.Id))
                {
                    notices.Add(new MarketNotice(MarketNoticeKind.BondMatured, date, bond.Id, 0));
                }

                _s.Bonds.Remove(bond);
            }
        }

        return notices;
    }

    /// <summary>Canje: los bonos del emisor en default se cambian por uno nuevo con quita. La tenencia hereda el costo.</summary>
    public List<MarketNotice> Restructure(string issuer, EconomicDate date)
    {
        var notices = new List<MarketNotice>();
        double recovery = _credit.Recovery(issuer);
        foreach (BondSeries old in _s.Bonds.Where(b => b.Issuer == issuer).ToList())
        {
            double cer = old.Type == BondType.Cer ? _inflation.Index / old.CerBase : 1;
            var exchange = new BondSeries
            {
                Id = $"{old.Id}R",
                Prefix = old.Prefix,
                Issuer = issuer,
                Type = BondType.Fixed,
                IssueDate = date.FiscalPeriod.FirstDay,
                TenorSeasons = Rules.RestructuredTenor,
                CouponRate = Rules.RestructuredCoupon,
                AmortizationInstallments = Rules.RestructuredInstallments,
                CerBase = _inflation.Index,
                IsRestructured = true,
                Currency = old.Currency,
            };
            _s.Bonds.Remove(old);
            _s.Bonds.Add(exchange);

            if (_s.Broker.Bonds.Remove(old.Id, out Holding? holding))
            {
                long quantity = (long)Math.Floor(holding.Quantity * old.Outstanding * recovery * cer);
                if (quantity > 0)
                {
                    _s.Broker.Bonds[exchange.Id] = new Holding { Quantity = quantity, CostBasis = holding.CostBasis };
                }
                else
                {
                    // Nada que recibir: todo el costo es pérdida.
                    _ledger.Post(LedgerEntities.Jugador, date,
                        new EntryText($"Canje de {old.Id}", "entry.bond-exchange-loss", new Dictionary<string, string> { ["instrument"] = old.Id }),
                        EntrySources.Ajuste,
                        JournalLine.ToDebit(SystemAccounts.ResultadosVentaNegativos, holding.CostBasis),
                        JournalLine.ToCredit(SystemAccounts.Bonos, holding.CostBasis));
                }

                notices.Add(new MarketNotice(MarketNoticeKind.Exchange, date, $"{old.Id}>{exchange.Id}", quantity));
            }
        }

        return notices;
    }

    /// <summary>Saca de circulación las series de un emisor que quebró: la tenencia se pierde entera.</summary>
    public List<MarketNotice> WriteOffIssuer(string issuer, EconomicDate date)
    {
        var notices = new List<MarketNotice>();
        foreach (BondSeries bond in _s.Bonds.Where(b => b.Issuer == issuer).ToList())
        {
            if (_s.Broker.Bonds.Remove(bond.Id, out Holding? holding) && holding.CostBasis > 0)
            {
                _ledger.Post(LedgerEntities.Jugador, date,
                    new EntryText($"Quiebra del emisor de {bond.Id}", "entry.bond-exchange-loss", new Dictionary<string, string> { ["instrument"] = bond.Id }),
                    EntrySources.Ajuste,
                    JournalLine.ToDebit(SystemAccounts.ResultadosVentaNegativos, holding.CostBasis),
                    JournalLine.ToCredit(SystemAccounts.Bonos, holding.CostBasis));
                notices.Add(new MarketNotice(MarketNoticeKind.Exchange, date, bond.Id, 0));
            }

            _s.Bonds.Remove(bond);
        }

        return notices;
    }

    /// <summary>Agrega una serie emitida por una sociedad del jugador.</summary>
    public void AddSeries(BondSeries series) => _s.Bonds.Add(series);

    private MarketNotice Collect(BondSeries bond, Holding holding, BondCashFlow flow, EconomicDate date)
    {
        // Los bonos en oro gotoro pagan en GTD: se acreditan en esa cuenta y se contabilizan al oficial.
        decimal fx = bond.IsGotoro ? (decimal)(Fx?.Official ?? 1) : 1;
        decimal coupon = MoneyRounding.Round((decimal)flow.Coupon * holding.Quantity * fx);
        decimal amortization = MoneyRounding.Round((decimal)flow.Amortization * holding.Quantity * fx);
        if (bond.IsGotoro)
        {
            _s.Broker.Gtd += MoneyRounding.Round((decimal)flow.Total * holding.Quantity);
        }

        double outstandingBefore = bond.OutstandingBefore(flow.Payment);
        decimal costPortion = flow.Amortization <= 0 ? 0
            : bond.PaymentsMade + 1 >= bond.TenorSeasons ? holding.CostBasis
            : MoneyRounding.Round(holding.CostBasis * (decimal)(bond.AmortizationFraction(flow.Payment) / outstandingBefore));
        decimal result = amortization - costPortion;

        var lines = new List<JournalLine> { JournalLine.ToDebit(bond.IsGotoro ? SystemAccounts.CuentaGotoro : SystemAccounts.CuentaComitente, coupon + amortization) };
        if (coupon > 0)
        {
            lines.Add(JournalLine.ToCredit(SystemAccounts.InteresesGanados, coupon));
        }

        if (costPortion > 0)
        {
            lines.Add(JournalLine.ToCredit(SystemAccounts.Bonos, costPortion));
        }

        if (result > 0)
        {
            lines.Add(JournalLine.ToCredit(SystemAccounts.ResultadosVentaPositivos, result));
        }
        else if (result < 0)
        {
            lines.Add(JournalLine.ToDebit(SystemAccounts.ResultadosVentaNegativos, -result));
        }

        if (coupon + amortization > 0 || costPortion > 0)
        {
            _ledger.Post(LedgerEntities.Jugador, date,
                new EntryText($"Cobro de {bond.Id}", "entry.bond-payment", new Dictionary<string, string>
                {
                    ["instrument"] = bond.Id,
                    ["payment"] = flow.Payment.ToString(CultureInfo.InvariantCulture),
                }),
                EntrySources.Cobro,
                lines.ToArray());
        }

        holding.CostBasis -= costPortion;
        return new MarketNotice(MarketNoticeKind.Coupon, date, bond.Id, coupon + amortization);
    }
}
