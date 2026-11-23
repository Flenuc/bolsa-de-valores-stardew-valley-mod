using ZuzuMarket.Core.Ledger;
using ZuzuMarket.Core.Macro;
using ZuzuMarket.Core.Money;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Markets;

/// <summary>
/// Oro gotoro en Valle Inversiones (§5.3): cuenta en GTD (contabilizada al oficial y revaluada cada día), dólar
/// oficial con cupo y recargo, MEP con bonos ley Gotoro y parking, depósito de billetes.
/// </summary>
public sealed partial class Broker
{
    /// <summary>Tipo de cambio (lo conecta la simulación).</summary>
    public ExchangeRateModel? Fx { get; set; }

    /// <summary>Reglas cambiarias de la dificultad actual.</summary>
    public Func<FxDifficultyRules> FxRules { get; set; } = () => new FxDifficultyRules();

    public decimal Gtd => _s.Broker.Gtd;

    public decimal Official => (decimal)(Fx?.Official ?? 1);

    public decimal Mep => (decimal)(Fx?.Mep ?? 1);

    /// <summary>Precio de compra del oficial (con recargo si hay cepo, si no con medio spread).</summary>
    public decimal OfficialAsk => MoneyRounding.Round(Official * (FxRules().Cepo ? 1 + FxRules().Surcharge : 1 + (FxRules().Spread / 2)));

    /// <summary>Precio de venta del oficial.</summary>
    public decimal OfficialBid => MoneyRounding.Round(Official * (1 - (FxRules().Cepo ? FxRules().Spread : FxRules().Spread / 2)));

    /// <summary>Cupo de oficial que queda en la estación (sin cepo, ilimitado).</summary>
    public decimal QuotaLeft(EconomicDate date)
    {
        FxDifficultyRules r = FxRules();
        if (!r.Cepo)
        {
            return decimal.MaxValue;
        }

        return _s.Broker.OfficialPeriod == date.FiscalPeriod ? Math.Max(0, r.Quota - _s.Broker.OfficialBought) : r.Quota;
    }

    private TradeError ValidateFx(decimal gtd, EconomicDate date, int timeOfDay)
    {
        if (!IsOpen)
        {
            return TradeError.NoAccount;
        }

        if (gtd <= 0)
        {
            return TradeError.InvalidQuantity;
        }

        return !_forcing && !IsMarketOpen(date, timeOfDay) ? TradeError.MarketClosed : TradeError.None;
    }

    /// <summary>Compra oro gotoro al oficial con la plata de la cuenta comitente (con cupo y recargo si hay cepo).</summary>
    public TradeError BuyOfficial(decimal gtd, EconomicDate date, int timeOfDay)
    {
        gtd = Math.Floor(gtd);
        TradeError error = ValidateFx(gtd, date, timeOfDay);
        if (error != TradeError.None)
        {
            return error;
        }

        if (IsInhibited())
        {
            return TradeError.Inhibited;
        }

        if (gtd > QuotaLeft(date))
        {
            return TradeError.AboveLimit;
        }

        decimal value = MoneyRounding.Round(gtd * Official);
        decimal total = MoneyRounding.Round(gtd * OfficialAsk);
        if (total > Cash)
        {
            return TradeError.InsufficientFunds;
        }

        Post(date, "Compra de oro gotoro al oficial", "entry.fx-buy-official", "",
            JournalLine.ToDebit(SystemAccounts.CuentaGotoro, value),
            JournalLine.ToDebit(FxRules().Cepo ? SystemAccounts.ImpuestoDivisas : SystemAccounts.ComisionesBursatiles, total - value),
            JournalLine.ToCredit(SystemAccounts.CuentaComitente, total));
        _s.Broker.Gtd += gtd;
        if (_s.Broker.OfficialPeriod != date.FiscalPeriod)
        {
            _s.Broker.OfficialPeriod = date.FiscalPeriod;
            _s.Broker.OfficialBought = 0;
        }

        _s.Broker.OfficialBought += gtd;
        return TradeError.None;
    }

    /// <summary>Vende oro gotoro al oficial (sin recargo).</summary>
    public TradeError SellOfficial(decimal gtd, EconomicDate date, int timeOfDay)
    {
        gtd = Math.Floor(gtd);
        TradeError error = ValidateFx(gtd, date, timeOfDay);
        if (error != TradeError.None)
        {
            return error;
        }

        if (gtd > Gtd)
        {
            return TradeError.InsufficientFunds;
        }

        decimal value = MoneyRounding.Round(gtd * Official);
        decimal proceeds = MoneyRounding.Round(gtd * OfficialBid);
        Post(date, "Venta de oro gotoro al oficial", "entry.fx-sell-official", "",
            JournalLine.ToDebit(SystemAccounts.CuentaComitente, proceeds),
            JournalLine.ToDebit(SystemAccounts.ComisionesBursatiles, value - proceeds),
            JournalLine.ToCredit(SystemAccounts.CuentaGotoro, value));
        _s.Broker.Gtd -= gtd;
        return TradeError.None;
    }

    /// <summary>Depósito de billetes gotoro (del colchón o de Krobus) en la cuenta.</summary>
    public TradeError DepositNotes(int notes, EconomicDate date)
    {
        if (!IsOpen)
        {
            return TradeError.NoAccount;
        }

        if (notes <= 0)
        {
            return TradeError.InvalidQuantity;
        }

        decimal value = MoneyRounding.Round(notes * Official);
        Post(date, "Depósito de billetes gotoro", "entry.fx-deposit-notes", "",
            JournalLine.ToDebit(SystemAccounts.CuentaGotoro, value),
            JournalLine.ToCredit(SystemAccounts.Capital, value));
        _s.Broker.Gtd += notes;
        return TradeError.None;
    }

    /// <summary>Revalúa la cuenta en oro gotoro al oficial del día (diferencia de cambio).</summary>
    public void RevalueGotoro(EconomicDate date)
    {
        decimal target = MoneyRounding.Round(_s.Broker.Gtd * Official);
        decimal current = _ledger.GetBalance(Entity, SystemAccounts.CuentaGotoro);
        decimal diff = target - current;
        if (Math.Abs(diff) < 0.01m)
        {
            return;
        }

        Post(date, "Diferencia de cambio", "entry.fx-revaluation", "",
            diff > 0 ? JournalLine.ToDebit(SystemAccounts.CuentaGotoro, diff) : JournalLine.ToDebit(SystemAccounts.DiferenciaCambioNegativa, -diff),
            diff > 0 ? JournalLine.ToCredit(SystemAccounts.DiferenciaCambioPositiva, diff) : JournalLine.ToCredit(SystemAccounts.CuentaGotoro, -diff));
    }

    // ───────────────────────────── MEP ─────────────────────────────

    /// <summary>Bono ley Gotoro que se usa para el MEP (el más corto vigente, sin default).</summary>
    public BondSeries? MepBond => _s.Bonds.Where(b => b.IsGotoro && !IsDefaulted(b)).OrderBy(b => b.Maturity).FirstOrDefault();

    /// <summary>Día hábil en que se completa un MEP iniciado hoy.</summary>
    public EconomicDate MepSettlement(EconomicDate date)
    {
        EconomicDate d = date;
        int days = Math.Max(1, FxRules().ParkingDays);
        while (days > 0)
        {
            d = d.AddDays(1);
            if (d.IsBusinessDay)
            {
                days--;
            }
        }

        return d;
    }

    /// <summary>
    /// MEP de oro local a oro gotoro: compra el bono en oro local ahora; la venta en GTD se hace sola al terminar el
    /// parking. Devuelve el error de la compra.
    /// </summary>
    public TradeError BuyMep(decimal localAmount, EconomicDate date, int timeOfDay)
    {
        if (MepBond is not { } bond)
        {
            return TradeError.UnknownInstrument;
        }

        long units = (long)Math.Floor(localAmount / (decimal)Math.Max(0.01, BondPrice(bond, date)));
        TradeQuote q = Execute(bond.Id, TradeSide.Buy, units, date, timeOfDay);
        if (q.Error == TradeError.None)
        {
            _s.Broker.MepOrders.Add(new MepOrder { Id = _s.Broker.NextId++, BondId = bond.Id, Units = units, ToGotoro = true, SettleOn = MepSettlement(date) });
        }

        return q.Error;
    }

    /// <summary>MEP de oro gotoro a oro local: compra el bono con GTD ahora y lo vende en oro local al terminar el parking.</summary>
    public TradeError SellMep(decimal gtd, EconomicDate date, int timeOfDay)
    {
        TradeError error = ValidateFx(gtd, date, timeOfDay);
        if (error != TradeError.None)
        {
            return error;
        }

        if (MepBond is not { } bond)
        {
            return TradeError.UnknownInstrument;
        }

        if (gtd > Gtd)
        {
            return TradeError.InsufficientFunds;
        }

        double price = BondNativePrice(bond, date);
        long units = (long)Math.Floor(gtd / (decimal)Math.Max(0.01, price));
        if (units <= 0)
        {
            return TradeError.InvalidQuantity;
        }

        decimal paidGtd = MoneyRounding.Round((decimal)price * units);
        decimal cost = MoneyRounding.Round(paidGtd * Official);
        Post(date, $"Compra de {bond.Id} en oro gotoro (MEP)", "entry.fx-mep", "",
            JournalLine.ToDebit(SystemAccounts.Bonos, cost),
            JournalLine.ToCredit(SystemAccounts.CuentaGotoro, cost));
        _s.Broker.Gtd -= paidGtd;
        AddHolding(_s.Broker.Bonds, bond.Id, units, cost);
        _s.Broker.MepOrders.Add(new MepOrder { Id = _s.Broker.NextId++, BondId = bond.Id, Units = units, ToGotoro = false, SettleOn = MepSettlement(date) });
        return TradeError.None;
    }

    /// <summary>Completa las segundas patas de los MEP que terminaron el parking.</summary>
    public List<MarketNotice> SettleMep(EconomicDate date)
    {
        var notices = new List<MarketNotice>();
        if (!date.IsBusinessDay)
        {
            return notices;
        }

        foreach (MepOrder order in _s.Broker.MepOrders.Where(o => o.SettleOn <= date).ToList())
        {
            _s.Broker.MepOrders.Remove(order);
            if (FindBond(order.BondId) is not { } bond || !_s.Broker.Bonds.TryGetValue(order.BondId, out Holding? holding))
            {
                continue;
            }

            long units = Math.Min(order.Units, holding.Quantity);
            if (units <= 0)
            {
                continue;
            }

            if (order.ToGotoro)
            {
                decimal gtd = MoneyRounding.Round((decimal)BondNativePrice(bond, date) * units * (1 - Rules.CommissionRate));
                decimal value = MoneyRounding.Round(gtd * Official);
                decimal cost = MoneyRounding.Round(holding.CostBasis * units / holding.Quantity);
                var lines = new List<JournalLine> { JournalLine.ToDebit(SystemAccounts.CuentaGotoro, value), JournalLine.ToCredit(SystemAccounts.Bonos, cost) };
                if (value > cost)
                {
                    lines.Add(JournalLine.ToCredit(SystemAccounts.ResultadosVentaPositivos, value - cost));
                }
                else if (value < cost)
                {
                    lines.Add(JournalLine.ToDebit(SystemAccounts.ResultadosVentaNegativos, cost - value));
                }

                Post(date, $"Venta de {bond.Id} en oro gotoro (MEP)", "entry.fx-mep", "", lines.ToArray());
                _s.Broker.Gtd += gtd;
                holding.CostBasis -= cost;
                holding.Quantity -= units;
                if (holding.Quantity <= 0)
                {
                    _s.Broker.Bonds.Remove(order.BondId);
                }

                notices.Add(new MarketNotice(MarketNoticeKind.MepSettled, date, bond.Id, gtd));
            }
            else
            {
                bool wasForcing = _forcing;
                _forcing = true;
                try
                {
                    TradeQuote q = Execute(bond.Id, TradeSide.Sell, units, date, MarketHours.Open);
                    notices.Add(new MarketNotice(MarketNoticeKind.MepSettled, date, bond.Id, q.Error == TradeError.None ? -q.Total : 0));
                }
                finally
                {
                    _forcing = wasForcing;
                }
            }
        }

        return notices;
    }

    /// <summary>Valor de la cuenta en oro gotoro, en oro local al oficial.</summary>
    public decimal GotoroValue => MoneyRounding.Round(Gtd * Official);

    private static void AddHolding(Dictionary<string, Holding> holdings, string id, long units, decimal cost)
    {
        if (!holdings.TryGetValue(id, out Holding? h))
        {
            holdings[id] = h = new Holding();
        }

        h.Quantity += units;
        h.CostBasis += cost;
    }
}
