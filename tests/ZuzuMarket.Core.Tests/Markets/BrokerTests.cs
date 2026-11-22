using ZuzuMarket.Core.Ledger;
using ZuzuMarket.Core.Markets;
using ZuzuMarket.Core.Money;
using ZuzuMarket.Core.Simulation;
using ZuzuMarket.Core.State;
using ZuzuMarket.Core.Taxation;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Tests.Markets;

public class BrokerTests
{
    private const int TenAm = 1000;
    private static readonly EconomicDate Monday = new(1, Season.Spring, 1);

    /// <summary>Partida sin inflación ni eventos, con la cuenta abierta y 5.000g depositados.</summary>
    private static EconomySimulation NewAccount(long cash = 10_000, decimal deposit = 5_000)
    {
        EconomySimulation sim = EconomySimulation.StartNewGame(TestData.WithoutInflation(), 1, Monday, "realista", cash);
        sim.Broker.Open(Monday);
        Assert.Equal(TradeError.None, sim.Broker.Deposit(deposit, Monday));
        return sim;
    }

    private static decimal Balance(EconomySimulation sim, string account) => sim.Ledger.GetBalance(LedgerEntities.Jugador, account);

    private static void AssertBooks(EconomySimulation sim)
    {
        IReadOnlyList<TrialBalanceRow> rows = sim.Ledger.GetTrialBalance(LedgerEntities.Jugador);
        Assert.Equal(rows.Sum(r => r.Debits), rows.Sum(r => r.Credits));
        BrokerState b = sim.Broker.State;
        Assert.Equal(b.Stocks.Values.Sum(h => h.CostBasis), Balance(sim, SystemAccounts.Acciones));
        Assert.Equal(b.Letras.Values.Sum(h => h.CostBasis), Balance(sim, SystemAccounts.Letras));
        Assert.Equal(b.Cauciones.Sum(c => c.Principal), Balance(sim, SystemAccounts.CaucionesColocadas));
        Assert.True(sim.Broker.Cash >= 0);
    }

    [Fact]
    public void DepositAndWithdraw_MoveGoldBetweenCajaAndComitente()
    {
        EconomySimulation sim = NewAccount();
        Assert.Equal(5_000m, sim.Broker.Cash);
        Assert.Equal(5_000m, Balance(sim, SystemAccounts.Caja));

        Assert.Equal(TradeError.InsufficientFunds, sim.Broker.Withdraw(6_000, Monday));
        Assert.Equal(TradeError.None, sim.Broker.Withdraw(1_000, Monday));
        Assert.Equal(4_000m, sim.Broker.Cash);
        Assert.Equal(6_000m, Balance(sim, SystemAccounts.Caja));
        AssertBooks(sim);

        EconomySimulation closed = EconomySimulation.StartNewGame(TestData.Real, 1, Monday, "realista", 100);
        Assert.Equal(TradeError.NoAccount, closed.Broker.Deposit(10, Monday));
    }

    [Fact]
    public void BuyQuote_IncludesSpreadImpactCommissionFeeAndIva()
    {
        EconomySimulation sim = NewAccount();
        double mid = sim.Stocks.PriceAt("JOJA", TenAm);
        TradeQuote q = sim.Broker.Quote("joja", TradeSide.Buy, 10, Monday, TenAm);

        Assert.True(q.IsValid);
        Assert.Equal("JOJA", q.Instrument);
        decimal expectedUnit = MoneyRounding.Round((decimal)(mid * (1 + 0.0025 + (0.1 * 10 / 30000.0))));
        Assert.Equal(expectedUnit, q.UnitPrice);
        Assert.Equal(expectedUnit * 10, q.Gross);
        Assert.Equal(MoneyRounding.Round(Math.Max(q.Gross * 0.005m, 1m)), q.Commission);
        Assert.Equal(MoneyRounding.Round(q.Gross * 0.0008m), q.MarketFee);
        Assert.Equal(MoneyRounding.Round(q.Commission * 0.21m), q.Iva);
        Assert.Equal(q.Gross + q.Commission + q.MarketFee + q.Iva, q.Total);

        TradeQuote sell = sim.Broker.Quote("JOJA", TradeSide.Sell, 10, Monday, TenAm);
        Assert.True(sell.UnitPrice < q.UnitPrice);
        Assert.Equal(TradeError.InsufficientHoldings, sell.Error);
    }

    [Fact]
    public void BigOrders_MoveThePriceMore()
    {
        EconomySimulation sim = NewAccount(cash: 2_000_000, deposit: 2_000_000);
        TradeQuote small = sim.Broker.Quote("GEMS", TradeSide.Buy, 10, Monday, TenAm);
        TradeQuote big = sim.Broker.Quote("GEMS", TradeSide.Buy, 5_000, Monday, TenAm);
        Assert.True(big.UnitPrice > small.UnitPrice * 1.04m);
    }

    [Fact]
    public void BuyAndSell_TrackCostBasisAndRealizedResult()
    {
        EconomySimulation sim = NewAccount();
        TradeQuote buy = sim.Broker.Execute("CALI", TradeSide.Buy, 20, Monday, TenAm);
        Assert.True(buy.IsValid);
        Holding holding = sim.Broker.State.Stocks["CALI"];
        Assert.Equal(20, holding.Quantity);
        Assert.Equal(buy.Gross, holding.CostBasis);
        Assert.Equal(5_000m - buy.Total, sim.Broker.Cash);
        Assert.Equal(buy.Fees, Balance(sim, SystemAccounts.ComisionesBursatiles)); // monotributista: el IVA es gasto
        AssertBooks(sim);

        TradeQuote sell = sim.Broker.Execute("CALI", TradeSide.Sell, 5, Monday, TenAm);
        Assert.True(sell.IsValid);
        decimal soldCost = MoneyRounding.Round(buy.Gross * 5 / 20);
        Assert.Equal(15, sim.Broker.State.Stocks["CALI"].Quantity);
        Assert.Equal(buy.Gross - soldCost, sim.Broker.State.Stocks["CALI"].CostBasis);
        // Mismo precio medio: se vende por debajo de lo que se compró (spread) → pérdida.
        Assert.Equal(soldCost - sell.Gross, Balance(sim, SystemAccounts.ResultadosVentaNegativos));
        AssertBooks(sim);

        sim.Broker.Execute("CALI", TradeSide.Sell, 15, Monday, TenAm);
        Assert.False(sim.Broker.State.Stocks.ContainsKey("CALI"));
        Assert.Equal(0m, Balance(sim, SystemAccounts.Acciones));
        Assert.Equal(3, sim.Broker.State.Trades.Count);
        AssertBooks(sim);
    }

    [Fact]
    public void Orders_AreRejectedWhenInvalid()
    {
        EconomySimulation sim = NewAccount(deposit: 50);
        Assert.Equal(TradeError.MarketClosed, sim.Broker.Quote("JOJA", TradeSide.Buy, 1, Monday, 850).Error);
        Assert.Equal(TradeError.MarketClosed, sim.Broker.Quote("JOJA", TradeSide.Buy, 1, Monday, 1700).Error);
        Assert.Equal(TradeError.MarketClosed, sim.Broker.Quote("JOJA", TradeSide.Buy, 1, new EconomicDate(1, Season.Spring, 6), TenAm).Error);
        Assert.Equal(TradeError.InvalidQuantity, sim.Broker.Quote("JOJA", TradeSide.Buy, 0, Monday, TenAm).Error);
        Assert.Equal(TradeError.UnknownInstrument, sim.Broker.Quote("NOPE", TradeSide.Buy, 1, Monday, TenAm).Error);
        Assert.Equal(TradeError.InsufficientFunds, sim.Broker.Quote("BZUZ", TradeSide.Buy, 10, Monday, TenAm).Error);
        Assert.False(sim.Broker.Execute("BZUZ", TradeSide.Buy, 10, Monday, TenAm).IsValid);
        Assert.Empty(sim.Broker.State.Trades);
        Assert.Equal(50m, sim.Broker.Cash);
    }

    [Fact]
    public void ResponsableInscripto_GetsIvaCreditOnCommissions()
    {
        EconomySimulation sim = NewAccount();
        sim.OnDayStarted(Monday);
        sim.Taxes.SetRegime(TaxRegime.ResponsableInscripto, Monday);
        TradeQuote buy = sim.Broker.Execute("JOJA", TradeSide.Buy, 10, Monday, TenAm);
        Assert.Equal(buy.Iva, Balance(sim, SystemAccounts.IvaCreditoFiscal));
        Assert.Equal(buy.Commission + buy.MarketFee, Balance(sim, SystemAccounts.ComisionesBursatiles));
        AssertBooks(sim);
    }

    [Fact]
    public void Letras_AreIssuedPricedAndPaidAtMaturity()
    {
        EconomySimulation sim = NewAccount();
        IReadOnlyList<LetraSeries> letras = sim.Broker.ActiveLetras(Monday);
        Assert.Equal(new[] { "LEFE28-P1", "LEFE56-P1", "LEFE112-P1" }, letras.Select(l => l.Id));
        Assert.Equal(new EconomicDate(1, Season.Summer, 1), letras[0].Maturity);

        double p28 = sim.Broker.LetraPrice(letras[0], Monday);
        double p56 = sim.Broker.LetraPrice(letras[1], Monday);
        double p112 = sim.Broker.LetraPrice(letras[2], Monday);
        Assert.True(p28 < 100 && p56 < p28 && p112 < p56);
        Assert.Equal(100 / (1 + sim.Broker.LetraRate(letras[0])), p28, 9);
        Assert.True(sim.Broker.LetraRate(letras[0]) > sim.Broker.ReferenceRate); // incluye el riesgo país

        TradeQuote buy = sim.Broker.Execute("LEFE28-P1", TradeSide.Buy, 10, Monday, TenAm);
        Assert.True(buy.IsValid);
        decimal cashAfterBuy = sim.Broker.Cash;

        // Acreción: el precio sube a medida que se acerca el vencimiento.
        sim.OnDayStarted(new EconomicDate(1, Season.Spring, 14));
        Assert.True(sim.Broker.LetraPrice(letras[0], sim.CurrentDate) > p28);

        DayStartedInfo maturity = sim.OnDayStarted(new EconomicDate(1, Season.Summer, 1));
        Assert.Contains(maturity.MarketNotices, n => n.Kind == MarketNoticeKind.LetraMatured && n.Amount == 1_000m);
        Assert.Equal(cashAfterBuy + 1_000m, sim.Broker.Cash);
        Assert.Equal(1_000m - buy.Gross, Balance(sim, SystemAccounts.InteresesGanados));
        Assert.Null(sim.Broker.FindLetra("LEFE28-P1"));
        Assert.NotNull(sim.Broker.FindLetra("LEFE28-V1")); // nueva licitación de verano
        AssertBooks(sim);
    }

    [Fact]
    public void Caucion_PaysInterestOnTheNextBusinessDay()
    {
        EconomySimulation sim = NewAccount();
        Assert.Equal(TradeError.BelowMinimum, sim.Broker.PlaceCaucion(50, 7, Monday, TenAm));
        Assert.Equal(TradeError.InvalidTerm, sim.Broker.PlaceCaucion(500, 5, Monday, TenAm));
        Assert.Equal(TradeError.MarketClosed, sim.Broker.PlaceCaucion(500, 7, Monday, 2000));

        double rate = sim.Broker.CaucionRate;
        Assert.Equal(TradeError.None, sim.Broker.PlaceCaucion(1_000, 7, Monday, TenAm));
        Caucion c = Assert.Single(sim.Broker.State.Cauciones);
        Assert.Equal(new EconomicDate(1, Season.Spring, 8), c.Maturity);
        Assert.Equal(MoneyRounding.Round(1_000m * (decimal)(rate * 7 / 28)), c.Interest);
        Assert.Equal(4_000m, sim.Broker.Cash);

        // Viernes a 1 día → vence el lunes (3 días de interés).
        var friday = new EconomicDate(1, Season.Spring, 5);
        TestData.AdvanceTo(sim, friday);
        sim.Broker.PlaceCaucion(1_000, 1, friday, TenAm);
        Assert.Equal(new EconomicDate(1, Season.Spring, 8), sim.Broker.State.Cauciones[^1].Maturity);

        DayStartedInfo monday = sim.OnDayStarted(new EconomicDate(1, Season.Spring, 8));
        Assert.Equal(2, monday.MarketNotices.Count(n => n.Kind == MarketNoticeKind.CaucionMatured));
        Assert.Empty(sim.Broker.State.Cauciones);
        Assert.True(Balance(sim, SystemAccounts.InteresesGanados) > 0);
        AssertBooks(sim);
    }

    [Fact]
    public void Dividends_ArePaidAtTheEndOfTheSeasonWithWithholding()
    {
        EconomySimulation sim = NewAccount();
        sim.Broker.Execute("ZUEN", TradeSide.Buy, 50, Monday, TenAm);
        TestData.AdvanceTo(sim, new EconomicDate(1, Season.Spring, 28));
        CompanyDefinition zuen = sim.Stocks.Find("ZUEN")!;
        decimal gross = MoneyRounding.Round((decimal)sim.Stocks.QuarterlyDividend(zuen) * 50);
        decimal cash = sim.Broker.Cash;

        DayStartedInfo summer = sim.OnDayStarted(new EconomicDate(1, Season.Summer, 1));
        decimal withheld = MoneyRounding.Round(gross * 0.07m);
        Assert.Contains(summer.MarketNotices, n => n.Kind == MarketNoticeKind.Dividend && n.Instrument == "ZUEN" && n.Amount == gross - withheld);
        Assert.Equal(cash + gross - withheld, sim.Broker.Cash);
        Assert.Equal(gross, Balance(sim, SystemAccounts.Dividendos));
        Assert.Equal(withheld, Balance(sim, SystemAccounts.RetencionDividendos));
        AssertBooks(sim);
    }

    [Fact]
    public void AYearOfRandomTrading_KeepsTheBooksConsistent()
    {
        EconomySimulation sim = NewAccount(cash: 100_000, deposit: 50_000);
        var rng = new ZuzuMarket.Core.Random.DeterministicRandom(42);
        string[] tickers = sim.Stocks.Companies.Select(c => c.Ticker).ToArray();
        for (int day = 0; day < EconomicDate.DaysPerYear; day++)
        {
            EconomicDate today = sim.CurrentDate;
            int time = 900 + (rng.NextInt(1, 8) * 100);
            for (int i = 0; i < 3; i++)
            {
                string ticker = tickers[rng.NextInt(0, tickers.Length)];
                TradeSide side = rng.NextBool() ? TradeSide.Buy : TradeSide.Sell;
                long qty = rng.NextInt(1, 40);
                if (side == TradeSide.Sell && sim.Broker.State.Stocks.TryGetValue(ticker, out Holding? h))
                {
                    qty = Math.Min(qty, h.Quantity);
                }

                sim.Broker.Execute(ticker, side, qty, today, time);
            }

            if (sim.Broker.ActiveLetras(today).FirstOrDefault() is { } letra && rng.NextBool(0.1))
            {
                sim.Broker.Execute(letra.Id, TradeSide.Buy, rng.NextInt(1, 10), today, time);
            }

            if (rng.NextBool(0.1))
            {
                sim.Broker.PlaceCaucion(rng.NextInt(100, 2000), sim.Broker.Rules.CaucionTerms[rng.NextInt(0, 4)], today, time);
            }

            sim.OnDayStarted(today.AddDays(1));
            AssertBooks(sim);
        }

        Assert.True(sim.Broker.State.Trades.Count > 100);
        Assert.True(Balance(sim, SystemAccounts.Dividendos) > 0);
    }

    [Fact]
    public void SaveAndLoad_KeepsTheAccountAndTheFuture()
    {
        EconomySimulation original = NewAccount();
        original.Broker.Execute("DEST", TradeSide.Buy, 15, Monday, TenAm);
        original.Broker.PlaceCaucion(500, 14, Monday, TenAm);
        TestData.AdvanceTo(original, new EconomicDate(1, Season.Spring, 10));

        EconomySimulation loaded = EconomySimulation.Load(TestData.WithoutInflation(), WorldStateSerializer.Deserialize(WorldStateSerializer.Serialize(original.Snapshot())));
        Assert.True(loaded.Broker.IsOpen);
        Assert.Equal(original.Broker.Cash, loaded.Broker.Cash);
        Assert.Equal(15, loaded.Broker.State.Stocks["DEST"].Quantity);
        Assert.Equal(original.Stocks.Get("DEST").Intraday, loaded.Stocks.Get("DEST").Intraday);

        var target = new EconomicDate(1, Season.Summer, 5);
        TestData.AdvanceTo(original, target);
        TestData.AdvanceTo(loaded, target);
        Assert.Equal(original.Stocks.Merzu, loaded.Stocks.Merzu);
        Assert.Equal(original.Broker.Cash, loaded.Broker.Cash);
        Assert.Equal(original.Broker.PortfolioValue(target, TenAm), loaded.Broker.PortfolioValue(target, TenAm));
    }
}
