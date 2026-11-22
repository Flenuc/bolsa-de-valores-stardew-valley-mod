using System.Globalization;
using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Ledger;
using ZuzuMarket.Core.Money;
using ZuzuMarket.Core.Taxation;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Markets;

/// <summary>Futuros MATZU: listado, precio, operatoria, diferencias diarias, entrega física y Silo de Acopio.</summary>
public sealed partial class Broker
{
    public DerivativesState Derivatives => _s.Derivatives;

    public DerivativesRules DerivativesRules => Rules.Derivatives;

    /// <summary>Último día hábil de la estación: vencimiento de futuros y opciones.</summary>
    public static EconomicDate ExpiryOf(FiscalPeriod period)
    {
        EconomicDate day = period.LastDay;
        while (!day.IsBusinessDay)
        {
            day = day.AddDays(-1);
        }

        return day;
    }

    public FuturesContract? FindFuture(string id) =>
        _s.Derivatives.Futures.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<FuturesContract> FuturesFor(string commodityId) =>
        _s.Derivatives.Futures.Where(f => f.CommodityId == commodityId).OrderBy(f => f.Expiry).ToList();

    public FuturesPosition? FuturesPositionFor(string contractId) =>
        _s.Derivatives.FuturesPositions.FirstOrDefault(p => p.ContractId == contractId);

    public int ContractSize(FuturesContract contract) =>
        _data.FindCommodity(contract.CommodityId)?.FuturesContractSize ?? 1;

    /// <summary>Precio futuro por unidad de referencia: precio vanilla × multiplicador real esperado × IPC proyectado.</summary>
    public double FuturePrice(FuturesContract contract, EconomicDate date)
    {
        CommodityDefinition commodity = _data.FindCommodity(contract.CommodityId)!;
        int days = Math.Max(0, date.DaysUntil(contract.Expiry));
        double impact = _events.PriceImpact(commodity, contract.Expiry);
        double real = _commodities.ExpectedRealMultiplier(commodity, date, contract.Expiry, impact);
        double ipc = _inflation.Index * Math.Pow(1 + _inflation.PeriodRate, days / (double)EconomicDate.DaysPerSeason);
        return commodity.ReferencePrice * real * ipc;
    }

    /// <summary>Lista los contratos de la estación actual y las siguientes, y las series de opciones.</summary>
    public void ListDerivatives(EconomicDate date, int timeOfDay = MarketHours.Open)
    {
        DerivativesState d = _s.Derivatives;
        for (int k = 0; k < DerivativesRules.FuturesMaturities; k++)
        {
            FiscalPeriod period = date.FiscalPeriod.AddSeasons(k);
            EconomicDate expiry = ExpiryOf(period);
            if (expiry < date)
            {
                continue;
            }

            foreach (CommodityDefinition commodity in _data.Commodities)
            {
                string id = $"{commodity.Id.ToUpperInvariant()}-{SeasonLetter(period.Season)}{period.Year}";
                if (FindFuture(id) is null)
                {
                    var contract = new FuturesContract { Id = id, CommodityId = commodity.Id, Period = period, Expiry = expiry };
                    contract.LastSettlement = contract.PreviousSettlement = FuturePrice(contract, date);
                    d.Futures.Add(contract);
                }
            }
        }

        ListOptions(date, timeOfDay);
        d.ListedFor = date.FiscalPeriod;
    }

    // ───────────────────────────── Operatoria ─────────────────────────────

    public DerivativeQuote QuoteFuture(string id, TradeSide side, long quantity, EconomicDate date, int timeOfDay)
    {
        FuturesContract? contract = FindFuture(id);
        if (contract is null || contract.Expiry < date)
        {
            return new DerivativeQuote(InstrumentKind.Future, id, side, quantity, 0, 0, 0, 0, 0, TradeError.UnknownInstrument);
        }

        double mid = FuturePrice(contract, date);
        double price = mid * (1 + ((side == TradeSide.Buy ? 1 : -1) * DerivativesRules.FuturesSpread / 2));
        int size = ContractSize(contract);
        decimal notional = MoneyRounding.Round((decimal)(price * size * Math.Max(0, quantity)));
        decimal commission = notional > 0 ? MoneyRounding.Round(Math.Max(notional * DerivativesRules.FuturesCommissionRate, Rules.MinimumCommission)) : 0;
        decimal iva = MoneyRounding.Round(commission * Rules.CommissionIvaRate);
        long current = FuturesPositionFor(contract.Id)?.Quantity ?? 0;
        long after = current + (side == TradeSide.Buy ? quantity : -quantity);
        decimal marginAfter = RequiredMargin(date, timeOfDay, contract.Id, after);

        TradeError error = ValidateDerivative(quantity, date, timeOfDay, commission + iva + Math.Max(0, marginAfter - MarginBalance), Math.Abs(after) > Math.Abs(current));
        return new DerivativeQuote(InstrumentKind.Future, contract.Id, side, quantity, price, notional, commission, iva, marginAfter, error);
    }

    public DerivativeQuote ExecuteFuture(string id, TradeSide side, long quantity, EconomicDate date, int timeOfDay)
    {
        DerivativeQuote q = QuoteFuture(id, side, quantity, date, timeOfDay);
        if (q.IsValid)
        {
            ApplyFutureTrade(q, date, timeOfDay);
        }

        return q;
    }

    private void ApplyFutureTrade(DerivativeQuote q, EconomicDate date, int timeOfDay)
    {
        FuturesContract contract = FindFuture(q.Instrument)!;
        int size = ContractSize(contract);
        FuturesPosition? p = FuturesPositionFor(contract.Id);
        if (p is null)
        {
            p = new FuturesPosition { ContractId = contract.Id, LastSettle = q.UnitPrice, EntryPrice = q.UnitPrice };
            _s.Derivatives.FuturesPositions.Add(p);
        }

        // La posición existente se ajusta al precio de la operación antes de cambiarla.
        PostVariation((q.UnitPrice - p.LastSettle) * size * p.Quantity, date, contract.Id);
        p.LastSettle = q.UnitPrice;

        long delta = q.Side == TradeSide.Buy ? q.Quantity : -q.Quantity;
        long after = p.Quantity + delta;
        if (p.Quantity == 0 || Math.Sign(after) != Math.Sign(p.Quantity))
        {
            p.EntryPrice = q.UnitPrice;
        }
        else if (Math.Abs(after) > Math.Abs(p.Quantity))
        {
            p.EntryPrice = ((p.EntryPrice * Math.Abs(p.Quantity)) + (q.UnitPrice * q.Quantity)) / Math.Abs(after);
        }

        p.Quantity = after;
        if (p.Quantity == 0)
        {
            _s.Derivatives.FuturesPositions.Remove(p);
        }

        PostDerivativeFees(q.Commission, q.Iva, date, q.Instrument);
        AdjustMargin(date, timeOfDay);
        _s.Broker.Trades.Add(new Trade(_s.Broker.NextId++, date, timeOfDay, InstrumentKind.Future, q.Instrument, q.Side, q.Quantity,
            MoneyRounding.Round((decimal)q.UnitPrice), q.Notional, q.Commission + q.Iva, q.Notional));
    }

    // ───────────────────────────── Diferencias diarias y entrega ─────────────────────────────

    /// <summary>Al cierre de cada día hábil: diferencias diarias contra la cuenta de garantías.</summary>
    public void SettleFutures(EconomicDate date)
    {
        foreach (FuturesContract contract in _s.Derivatives.Futures)
        {
            contract.PreviousSettlement = contract.LastSettlement;
            contract.LastSettlement = FuturePrice(contract, date);
        }

        foreach (FuturesPosition p in _s.Derivatives.FuturesPositions)
        {
            if (FindFuture(p.ContractId) is not { } contract)
            {
                continue;
            }

            PostVariation((contract.LastSettlement - p.LastSettle) * ContractSize(contract) * p.Quantity, date, contract.Id);
            p.LastSettle = contract.LastSettlement;
        }
    }

    /// <summary>Entrega física de los contratos que vencen hoy (o antes, si se saltearon días).</summary>
    public List<MarketNotice> DeliverFutures(EconomicDate date)
    {
        var notices = new List<MarketNotice>();
        foreach (FuturesContract contract in _s.Derivatives.Futures.Where(c => c.Expiry <= date).ToList())
        {
            CommodityDefinition commodity = _data.FindCommodity(contract.CommodityId)!;
            double price = FuturePrice(contract, contract.Expiry <= date ? date : contract.Expiry);
            if (FuturesPositionFor(contract.Id) is { } p && p.Quantity != 0)
            {
                PostVariation((price - p.LastSettle) * ContractSize(contract) * p.Quantity, date, contract.Id);
                long units = Math.Abs(p.Quantity) * ContractSize(contract);
                if (p.Quantity < 0)
                {
                    notices.AddRange(DeliverShort(contract, commodity, units, price, date));
                }
                else
                {
                    notices.Add(ReceiveLong(contract, commodity, units, price, date));
                }

                _s.Derivatives.FuturesPositions.Remove(p);
            }

            _s.Derivatives.Futures.Remove(contract);
        }

        return notices;
    }

    private IEnumerable<MarketNotice> DeliverShort(FuturesContract contract, CommodityDefinition commodity, long units, double price, EconomicDate date)
    {
        long remaining = units;
        decimal gross = 0;
        foreach (SiloItem item in _s.Derivatives.Silo.Where(i => commodity.ItemIds.Contains(i.ItemId)).ToList())
        {
            int take = (int)Math.Min(remaining, item.Stack);
            if (take <= 0)
            {
                break;
            }

            double ratio = item.BasePrice > 0 ? item.BasePrice / commodity.ReferencePrice : 1;
            gross += (decimal)(price * ratio * (1 + DerivativesRules.QualityPremium(item.Quality)) * take);
            _commodities.RecordShipment(item.ItemId, take);
            item.Stack -= take;
            remaining -= take;
            if (item.Stack == 0)
            {
                _s.Derivatives.Silo.Remove(item);
            }
        }

        long delivered = units - remaining;
        if (delivered > 0)
        {
            string activity = _taxes.ActivityFor(commodity.ItemIds[0], 0);
            _taxes.RecordSale(date, activity, MoneyRounding.Round(gross), SaleChannel.Shop, SystemAccounts.CuentaComitente,
                new EntryText($"Entrega de {contract.Id}", "entry.futures-delivery", new Dictionary<string, string> { ["instrument"] = contract.Id }));
            yield return new MarketNotice(MarketNoticeKind.FuturesDelivered, date, contract.Id, MoneyRounding.Round(gross));
        }

        if (remaining > 0)
        {
            decimal penalty = MoneyRounding.Round((decimal)(price * remaining * DerivativesRules.DeliveryPenalty));
            Post(date, $"Multa por no entregar {contract.Id}", "entry.futures-penalty", contract.Id,
                JournalLine.ToDebit(SystemAccounts.ResultadosDerivadosNegativos, penalty),
                JournalLine.ToCredit(SystemAccounts.CuentaComitente, penalty));
            yield return new MarketNotice(MarketNoticeKind.FuturesShortfall, date, contract.Id, remaining);
        }
    }

    private MarketNotice ReceiveLong(FuturesContract contract, CommodityDefinition commodity, long units, double price, EconomicDate date)
    {
        ReleaseAllMargin(date);
        decimal cost = MoneyRounding.Round((decimal)(price * units));
        if (cost > Cash)
        {
            // Sin saldo para pagar la mercadería: se liquida por diferencia (ya ajustada) y no se recibe nada.
            return new MarketNotice(MarketNoticeKind.FuturesShortfall, date, contract.Id, -units);
        }

        _taxes.RecordPurchase(date, cost, SystemAccounts.CuentaComitente);
        AddToSilo(commodity.ItemIds[0], 0, (int)Math.Min(units, int.MaxValue), (int)Math.Round(commodity.ReferencePrice));
        return new MarketNotice(MarketNoticeKind.FuturesReceived, date, contract.Id, units);
    }

    private void PostVariation(double amount, EconomicDate date, string contractId)
    {
        decimal value = MoneyRounding.Round((decimal)amount);
        if (value > 0)
        {
            Post(date, $"Diferencia diaria {contractId}", "entry.futures-variation", contractId,
                JournalLine.ToDebit(SystemAccounts.Garantias, value),
                JournalLine.ToCredit(SystemAccounts.ResultadosDerivadosPositivos, value));
        }
        else if (value < 0)
        {
            Post(date, $"Diferencia diaria {contractId}", "entry.futures-variation", contractId,
                JournalLine.ToDebit(SystemAccounts.ResultadosDerivadosNegativos, -value),
                JournalLine.ToCredit(SystemAccounts.Garantias, -value));
        }
    }

    private void PostDerivativeFees(decimal commission, decimal iva, EconomicDate date, string instrument)
    {
        if (commission + iva <= 0)
        {
            return;
        }

        bool ri = _regime() == TaxRegime.ResponsableInscripto;
        var lines = new List<JournalLine> { JournalLine.ToDebit(SystemAccounts.ComisionesBursatiles, commission + (ri ? 0 : iva)) };
        if (ri && iva > 0)
        {
            lines.Add(JournalLine.ToDebit(SystemAccounts.IvaCreditoFiscal, iva));
        }

        lines.Add(JournalLine.ToCredit(SystemAccounts.CuentaComitente, commission + iva));
        Post(date, $"Comisión {instrument}", "entry.derivative-fee", instrument, lines.ToArray());
    }

    private void Post(EconomicDate date, string fallback, string key, string instrument, params JournalLine[] lines) =>
        _ledger.Post(LedgerEntities.Jugador, date,
            new EntryText(fallback, key, new Dictionary<string, string> { ["instrument"] = instrument }),
            EntrySources.Ajuste, lines);

    // ───────────────────────────── Silo de Acopio ─────────────────────────────

    public IReadOnlyList<SiloItem> Silo => _s.Derivatives.Silo;

    public bool IsSiloItem(string itemId) => _data.FindCommodityByItemId(itemId) is not null;

    public long SiloUnits(string commodityId) =>
        _data.FindCommodity(commodityId) is { } c ? _s.Derivatives.Silo.Where(i => c.ItemIds.Contains(i.ItemId)).Sum(i => (long)i.Stack) : 0;

    public void AddToSilo(string itemId, int quality, int stack, int basePrice)
    {
        if (stack <= 0)
        {
            return;
        }

        SiloItem? existing = _s.Derivatives.Silo.FirstOrDefault(i => i.ItemId == itemId && i.Quality == quality && i.BasePrice == basePrice);
        if (existing is not null)
        {
            existing.Stack += stack;
        }
        else
        {
            _s.Derivatives.Silo.Add(new SiloItem { ItemId = itemId, Quality = quality, Stack = stack, BasePrice = basePrice });
        }
    }

    /// <summary>Reemplaza el contenido del silo (lo usa el juego al cerrar el cofre).</summary>
    public void RemoveFromSilo(SiloItem item, int units)
    {
        item.Stack -= Math.Min(units, item.Stack);
        if (item.Stack <= 0)
        {
            _s.Derivatives.Silo.Remove(item);
        }
    }

    public void SetSilo(IEnumerable<SiloItem> items)
    {
        _s.Derivatives.Silo.Clear();
        foreach (SiloItem item in items.Where(i => i.Stack > 0 && IsSiloItem(i.ItemId)))
        {
            AddToSilo(item.ItemId, item.Quality, item.Stack, item.BasePrice);
        }
    }

    private static string ContractCode(string commodityId, FiscalPeriod period) =>
        string.Create(CultureInfo.InvariantCulture, $"{commodityId.ToUpperInvariant()}-{SeasonLetter(period.Season)}{period.Year}");
}
