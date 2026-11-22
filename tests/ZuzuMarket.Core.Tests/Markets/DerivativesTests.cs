using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Ledger;
using ZuzuMarket.Core.Markets;
using ZuzuMarket.Core.Money;
using ZuzuMarket.Core.Simulation;
using ZuzuMarket.Core.State;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Tests.Markets;

public class DerivativesTests
{
    private const int TenAm = 1000;
    private static readonly EconomicDate Monday = new(1, Season.Spring, 1);
    private static readonly EconomicDate SpringExpiry = new(1, Season.Spring, 26);

    /// <summary>Sin ruido (commodities y acciones), sin inflación y sin eventos aleatorios: cuentas exactas.</summary>
    private static GameData Quiet()
    {
        GameData data = TestData.WithoutInflation();
        foreach (CommodityDefinition c in data.Commodities)
        {
            c.Market.Volatility = 0;
        }

        foreach (CompanyDefinition c in data.Companies)
        {
            c.Volatility = 0.000001; // casi cero (Black-Scholes necesita volatilidad > 0)
        }

        return data;
    }

    private static EconomySimulation NewAccount(decimal deposit = 50_000, GameData? data = null, EconomicDate? start = null)
    {
        EconomicDate date = start ?? Monday;
        EconomySimulation sim = EconomySimulation.StartNewGame(data ?? Quiet(), 1, date, "realista", 200_000);
        sim.Broker.Open(date);
        sim.Broker.Deposit(deposit, date);
        return sim;
    }

    private static decimal Balance(EconomySimulation sim, string account) => sim.Ledger.GetBalance(LedgerEntities.Jugador, account);

    private static void AssertBooks(EconomySimulation sim)
    {
        IReadOnlyList<TrialBalanceRow> rows = sim.Ledger.GetTrialBalance(LedgerEntities.Jugador);
        Assert.Equal(rows.Sum(r => r.Debits), rows.Sum(r => r.Credits));
        var options = sim.Broker.Derivatives.OptionPositions;
        Assert.Equal(options.Where(p => p.Quantity > 0).Sum(p => p.PremiumBasis), Balance(sim, SystemAccounts.PrimasCompradas));
        Assert.Equal(options.Where(p => p.Quantity < 0).Sum(p => p.PremiumBasis), Balance(sim, SystemAccounts.PrimasLanzadas));
        Assert.Equal(sim.Broker.State.Stocks.Values.Sum(h => h.CostBasis), Balance(sim, SystemAccounts.Acciones));
    }

    // ───────────────────────────── Futuros: precio ─────────────────────────────

    [Fact]
    public void Contracts_AreListedForThreeSeasons()
    {
        EconomySimulation sim = NewAccount();
        IReadOnlyList<FuturesContract> wheat = sim.Broker.FuturesFor("trigo");
        Assert.Equal(new[] { "TRIGO-P1", "TRIGO-V1", "TRIGO-O1" }, wheat.Select(c => c.Id));
        Assert.Equal(SpringExpiry, wheat[0].Expiry); // el 28 es domingo: vence el viernes 26
        Assert.Equal(15 * 3, sim.Broker.Derivatives.Futures.Count);
    }

    [Fact]
    public void FuturePrice_ConvergesToTheSpotAtExpiry()
    {
        EconomySimulation sim = NewAccount();
        FuturesContract contract = sim.Broker.FindFuture("CHIRIVIA-P1")!;
        TestData.AdvanceTo(sim, SpringExpiry.AddDays(-1));
        double spot = 35 * sim.Market.Get("chirivia").Multiplier;
        Assert.Equal(spot, sim.Broker.FuturePrice(contract, SpringExpiry), 6);
    }

    [Fact]
    public void FuturePrice_ReflectsTheSeasonOfDelivery()
    {
        EconomySimulation sim = NewAccount(start: new EconomicDate(1, Season.Summer, 1));
        double summer = sim.Broker.FuturePrice(sim.Broker.FindFuture("CALABAZA-V1")!, sim.CurrentDate);
        double fall = sim.Broker.FuturePrice(sim.Broker.FindFuture("CALABAZA-O1")!, sim.CurrentDate);
        Assert.Equal(320 * 1.30, summer, 6); // fuera de estación
        Assert.Equal(320 * 0.95, fall, 6);   // cosecha de otoño
    }

    [Fact]
    public void ShortLivedEvents_MoveTheSpotButNotLaterContracts()
    {
        EconomySimulation sim = NewAccount(data: TestData.Load(), start: new EconomicDate(1, Season.Summer, 1));
        foreach (DifficultyDefinition d in sim.Data.Difficulties)
        {
            d.InflationMin = d.InflationMax = 0;
        }

        FuturesContract fall = sim.Broker.FindFuture("TRIGO-O1")!;
        double before = sim.Broker.FuturePrice(fall, sim.CurrentDate);
        double spotBefore = sim.Market.Get("trigo").RealMultiplier;
        sim.ForceEvent("langostas"); // dura 14 días: termina antes del vencimiento de otoño
        Assert.True(sim.Market.Get("trigo").RealMultiplier > spotBefore * 1.3);
        Assert.Equal(before, sim.Broker.FuturePrice(fall, sim.CurrentDate), 6);
    }

    // ───────────────────────────── Futuros: garantías ─────────────────────────────

    [Fact]
    public void OpeningAPosition_DepositsTheInitialMargin()
    {
        EconomySimulation sim = NewAccount();
        DerivativeQuote q = sim.Broker.ExecuteFuture("TRIGO-V1", TradeSide.Sell, 2, Monday, TenAm);
        Assert.True(q.IsValid);
        decimal expected = MoneyRounding.Round((decimal)(2 * 500 * sim.Broker.FuturePrice(sim.Broker.FindFuture("TRIGO-V1")!, Monday) * 0.15));
        Assert.Equal(expected, sim.Broker.MarginBalance);
        Assert.Equal(-2, sim.Broker.FuturesPositionFor("TRIGO-V1")!.Quantity);
        Assert.Equal(50_000m - expected - q.Commission - q.Iva, sim.Broker.Cash);
        AssertBooks(sim);

        // Cerrar devuelve la garantía.
        sim.Broker.ExecuteFuture("TRIGO-V1", TradeSide.Buy, 2, Monday, TenAm);
        Assert.Null(sim.Broker.FuturesPositionFor("TRIGO-V1"));
        Assert.Equal(0m, sim.Broker.MarginBalance);
        Assert.True(Balance(sim, SystemAccounts.ResultadosDerivadosNegativos) > 0); // perdió el spread
        AssertBooks(sim);
    }

    [Fact]
    public void DailyVariation_PaysTheWinnerAndChargesTheLoser()
    {
        EconomySimulation sim = NewAccount();
        sim.Broker.ExecuteFuture("PAPA-V1", TradeSide.Buy, 1, Monday, TenAm);
        FuturesContract contract = sim.Broker.FindFuture("PAPA-V1")!;
        double entry = sim.Broker.FuturesPositionFor("PAPA-V1")!.LastSettle;
        decimal margin = sim.Broker.MarginBalance;

        sim.ForceMarginTest("papa", 0.2); // sube el precio: el comprado gana
        double settle = contract.LastSettlement;
        decimal gain = MoneyRounding.Round((decimal)((settle - entry) * 100));
        Assert.True(gain > 0);
        Assert.Equal(gain, Balance(sim, SystemAccounts.ResultadosDerivadosPositivos));
        AssertBooks(sim);
    }

    [Fact]
    public void MarginCall_IsToppedUpAutomaticallyWhenThereIsCash()
    {
        EconomySimulation sim = NewAccount();
        sim.Broker.ExecuteFuture("MELON-P1", TradeSide.Sell, 3, Monday, TenAm);
        List<MarketNotice> notices = sim.ForceMarginTest("melon", 1.0); // sube: el vendido pierde
        Assert.Contains(notices, n => n.Kind == MarketNoticeKind.MarginTopUp);
        Assert.Null(sim.Broker.Derivatives.MarginCallDeadline);
        Assert.Equal(sim.Broker.RequiredMargin(Monday, 1700), sim.Broker.MarginBalance);
        AssertBooks(sim);
    }

    [Fact]
    public void MarginCall_WithoutCash_EndsInForcedLiquidation()
    {
        EconomySimulation sim = NewAccount(deposit: 3_000);
        DerivativeQuote open = sim.Broker.ExecuteFuture("MELON-P1", TradeSide.Sell, 1, Monday, TenAm);
        Assert.True(open.IsValid);
        sim.Broker.Withdraw(sim.Broker.Cash, Monday); // deja la cuenta sin saldo libre

        List<MarketNotice> call = sim.ForceMarginTest("melon", 1.0);
        Assert.Contains(call, n => n.Kind == MarketNoticeKind.MarginCall);
        Assert.Equal(new EconomicDate(1, Season.Spring, 2), sim.Broker.Derivatives.MarginCallDeadline);

        DayStartedInfo next = sim.OnDayStarted(new EconomicDate(1, Season.Spring, 2));
        Assert.Contains(next.MarketNotices, n => n.Kind == MarketNoticeKind.Liquidation);
        Assert.Null(sim.Broker.FuturesPositionFor("MELON-P1"));
        Assert.Null(sim.Broker.Derivatives.MarginCallDeadline);
        Assert.Equal(0m, sim.Broker.MarginBalance);
        AssertBooks(sim);
    }

    // ───────────────────────────── Futuros: entrega física ─────────────────────────────

    [Fact]
    public void ShortDelivery_SellsTheGoodsFromTheSiloWithQualityPremium()
    {
        EconomySimulation sim = NewAccount();
        sim.OnDayStarted(Monday);
        sim.Broker.AddToSilo("24", 2, 100, 35); // 100 chirivías de oro
        sim.Broker.ExecuteFuture("CHIRIVIA-P1", TradeSide.Sell, 1, Monday, TenAm);

        DayStartedInfo expiry = sim.OnDayStarted(SpringExpiry);
        double price = 35 * sim.Market.Get("chirivia").Multiplier;
        decimal expected = MoneyRounding.Round((decimal)(price * 1.2 * 100));
        Assert.Contains(expiry.MarketNotices, n => n.Kind == MarketNoticeKind.FuturesDelivered && n.Amount == expected);
        Assert.Equal(0, sim.Broker.SiloUnits("chirivia"));
        Assert.Equal(expected, sim.Taxes.State.Current.NetSalesByActivity["agro"]); // monotributista: sin IVA
        Assert.Null(sim.Broker.FindFuture("CHIRIVIA-P1"));
        Assert.Equal(0m, sim.Broker.MarginBalance);
        AssertBooks(sim);
    }

    [Fact]
    public void ShortDelivery_WithMissingGoods_PaysAPenalty()
    {
        EconomySimulation sim = NewAccount();
        sim.Broker.AddToSilo("24", 0, 60, 35);
        sim.Broker.ExecuteFuture("CHIRIVIA-P1", TradeSide.Sell, 1, Monday, TenAm);
        DayStartedInfo expiry = sim.OnDayStarted(SpringExpiry);
        Assert.Contains(expiry.MarketNotices, n => n.Kind == MarketNoticeKind.FuturesShortfall && n.Amount == 40);
        double price = 35 * sim.Market.Get("chirivia").Multiplier;
        JournalEntry penalty = Assert.Single(sim.Ledger.Entries, e => e.DescriptionKey == "entry.futures-penalty");
        Assert.Equal(MoneyRounding.Round((decimal)(price * 40 * 0.2)), penalty.TotalDebit);
        Assert.Contains(expiry.MarketNotices, n => n.Kind == MarketNoticeKind.FuturesDelivered); // las 60 que había sí se entregaron
        AssertBooks(sim);
    }

    [Fact]
    public void LongDelivery_PutsTheGoodsInTheSilo()
    {
        EconomySimulation sim = NewAccount();
        sim.Broker.ExecuteFuture("HUEVO-P1", TradeSide.Buy, 1, Monday, TenAm);
        DayStartedInfo expiry = sim.OnDayStarted(SpringExpiry);
        Assert.Contains(expiry.MarketNotices, n => n.Kind == MarketNoticeKind.FuturesReceived && n.Amount == 200);
        Assert.Equal(200, sim.Broker.SiloUnits("huevo"));
        Assert.True(Balance(sim, SystemAccounts.ComprasEnTiendas) > 0);
        AssertBooks(sim);
    }

    // ───────────────────────────── Opciones ─────────────────────────────

    [Fact]
    public void BlackScholes_MatchesReferenceValuesAndParity()
    {
        double call = BlackScholes.Price(OptionType.Call, 100, 100, 1, 0.05, 0.2);
        double put = BlackScholes.Price(OptionType.Put, 100, 100, 1, 0.05, 0.2);
        Assert.Equal(10.4506, call, 3);
        Assert.Equal(5.5735, put, 3);
        Assert.Equal(call - put, 100 - (100 * Math.Exp(-0.05)), 6);
        Assert.Equal(0.6368, BlackScholes.Delta(OptionType.Call, 100, 100, 1, 0.05, 0.2), 3);
    }

    [Fact]
    public void OptionChains_AreListedAroundThePrice()
    {
        EconomySimulation sim = NewAccount();
        IReadOnlyList<OptionSeries> chain = sim.Broker.OptionsFor("ZUEN", Monday.FiscalPeriod);
        Assert.Equal(new[] { 47.0, 49, 52, 55, 57 }, chain.Where(o => o.Type == OptionType.Call).Select(o => o.Strike));
        Assert.Equal(10, chain.Count);
        Assert.Equal(2, sim.Broker.OptionPeriods().Count);
    }

    [Fact]
    public void LongCallInTheMoney_IsExercisedWithDeliveryOfShares()
    {
        EconomySimulation sim = NewAccount();
        DerivativeQuote buy = sim.Broker.ExecuteOption("ZUEN-C47-P1", TradeSide.Buy, 2, Monday, TenAm);
        Assert.True(buy.IsValid);
        Assert.Equal(buy.Notional, Balance(sim, SystemAccounts.PrimasCompradas));
        AssertBooks(sim);

        DayStartedInfo expiry = sim.OnDayStarted(SpringExpiry);
        Assert.Contains(expiry.MarketNotices, n => n.Kind == MarketNoticeKind.OptionExercised && n.Instrument == "ZUEN-C47-P1" && n.Amount == 20);
        Assert.Equal(20, sim.Broker.State.Stocks["ZUEN"].Quantity);
        Assert.Equal(47m * 20, sim.Broker.State.Stocks["ZUEN"].CostBasis);
        Assert.Equal(0m, Balance(sim, SystemAccounts.PrimasCompradas));
        AssertBooks(sim);
    }

    [Fact]
    public void UncoveredShortPut_RequiresMarginAndBuysSharesAtTheStrike()
    {
        EconomySimulation sim = NewAccount();
        DerivativeQuote write = sim.Broker.ExecuteOption("ZUEN-P57-P1", TradeSide.Sell, 1, Monday, TenAm);
        Assert.True(write.IsValid);
        Assert.True(sim.Broker.MarginBalance > 0);
        Assert.Equal(write.Notional, Balance(sim, SystemAccounts.PrimasLanzadas));
        AssertBooks(sim);

        sim.OnDayStarted(SpringExpiry);
        Assert.Equal(10, sim.Broker.State.Stocks["ZUEN"].Quantity);
        Assert.Equal(570m, sim.Broker.State.Stocks["ZUEN"].CostBasis);
        Assert.Equal(0m, sim.Broker.MarginBalance);
        Assert.Equal(write.Notional, Balance(sim, SystemAccounts.ResultadosDerivadosPositivos));
        AssertBooks(sim);
    }

    [Fact]
    public void CoveredCall_BlocksTheSharesAndNeedsNoMargin()
    {
        EconomySimulation sim = NewAccount();
        sim.Broker.Execute("ZUEN", TradeSide.Buy, 10, Monday, TenAm);
        DerivativeQuote write = sim.Broker.ExecuteOption("ZUEN-C55-P1", TradeSide.Sell, 1, Monday, TenAm);
        Assert.True(write.IsValid);
        Assert.Equal(0m, sim.Broker.MarginBalance);
        Assert.Equal(10, sim.Broker.BlockedShares("ZUEN"));
        Assert.Equal(TradeError.InsufficientHoldings, sim.Broker.Quote("ZUEN", TradeSide.Sell, 1, Monday, TenAm).Error);

        sim.OnDayStarted(SpringExpiry); // fuera del dinero: vence sin valor y libera las acciones
        Assert.Equal(0, sim.Broker.BlockedShares("ZUEN"));
        Assert.Equal(10, sim.Broker.State.Stocks["ZUEN"].Quantity);
        Assert.Equal(write.Notional, Balance(sim, SystemAccounts.ResultadosDerivadosPositivos));
        AssertBooks(sim);
    }

    [Fact]
    public void ClosingAnOptionEarly_RealizesTheResult()
    {
        EconomySimulation sim = NewAccount();
        sim.Broker.ExecuteOption("ZUEN-C52-V1", TradeSide.Buy, 3, Monday, TenAm);
        DerivativeQuote sell = sim.Broker.ExecuteOption("ZUEN-C52-V1", TradeSide.Sell, 3, Monday, TenAm);
        Assert.True(sell.IsValid);
        Assert.Null(sim.Broker.OptionPositionFor("ZUEN-C52-V1"));
        Assert.True(Balance(sim, SystemAccounts.ResultadosDerivadosNegativos) > 0); // pagó el spread
        AssertBooks(sim);
    }

    // ───────────────────────────── Integración ─────────────────────────────

    [Fact]
    public void AYearOfRandomDerivatives_KeepsTheBooksBalanced()
    {
        EconomySimulation sim = NewAccount(deposit: 150_000, data: TestData.Load());
        var rng = new ZuzuMarket.Core.Random.DeterministicRandom(8);
        for (int day = 0; day < EconomicDate.DaysPerYear; day++)
        {
            EconomicDate today = sim.CurrentDate;
            var futures = sim.Broker.Derivatives.Futures;
            if (futures.Count > 0 && rng.NextBool(0.5))
            {
                FuturesContract c = futures[rng.NextInt(0, futures.Count)];
                sim.Broker.ExecuteFuture(c.Id, rng.NextBool() ? TradeSide.Buy : TradeSide.Sell, rng.NextInt(1, 3), today, TenAm);
            }

            var options = sim.Broker.Derivatives.Options;
            if (options.Count > 0 && rng.NextBool(0.5))
            {
                OptionSeries o = options[rng.NextInt(0, options.Count)];
                sim.Broker.ExecuteOption(o.Id, rng.NextBool() ? TradeSide.Buy : TradeSide.Sell, rng.NextInt(1, 4), today, TenAm);
            }

            if (rng.NextBool(0.1))
            {
                sim.Broker.AddToSilo("262", 0, rng.NextInt(100, 600), 25);
            }

            sim.OnDayStarted(today.AddDays(1));
            AssertBooks(sim);
        }

        Assert.True(sim.Broker.State.Trades.Count(t => t.Kind is InstrumentKind.Future or InstrumentKind.Option) > 50);
    }

    [Fact]
    public void SaveAndLoad_KeepsPositionsSiloAndMargin()
    {
        EconomySimulation sim = NewAccount();
        sim.Broker.AddToSilo("276", 1, 30, 320);
        sim.Broker.ExecuteFuture("CALABAZA-O1", TradeSide.Sell, 1, Monday, TenAm);
        sim.Broker.ExecuteOption("ZUEN-P49-V1", TradeSide.Sell, 2, Monday, TenAm);
        TestData.AdvanceTo(sim, new EconomicDate(1, Season.Spring, 10));

        EconomySimulation loaded = EconomySimulation.Load(Quiet(), WorldStateSerializer.Deserialize(WorldStateSerializer.Serialize(sim.Snapshot())));
        Assert.Equal(-1, loaded.Broker.FuturesPositionFor("CALABAZA-O1")!.Quantity);
        Assert.Equal(-2, loaded.Broker.OptionPositionFor("ZUEN-P49-V1")!.Quantity);
        Assert.Equal(30, loaded.Broker.SiloUnits("calabaza"));
        Assert.Equal(sim.Broker.MarginBalance, loaded.Broker.MarginBalance);
        Assert.Equal(sim.Broker.Derivatives.Futures.Count, loaded.Broker.Derivatives.Futures.Count);
    }
}
