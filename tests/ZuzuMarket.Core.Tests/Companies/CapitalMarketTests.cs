using ZuzuMarket.Core.Companies;
using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Ledger;
using ZuzuMarket.Core.Markets;
using ZuzuMarket.Core.Simulation;
using ZuzuMarket.Core.State;
using ZuzuMarket.Core.Taxation;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Tests.Companies;

public class CapitalMarketTests
{
    private static readonly EconomicDate Monday = new(1, Season.Spring, 1);
    private const string CopperOre = "378";
    private const int TenAm = 1000;

    private static decimal Balance(EconomySimulation sim, string entity, string account) => sim.Ledger.GetBalance(entity, account);

    private static void AssertBalanced(EconomySimulation sim)
    {
        foreach (string entity in new[] { LedgerEntities.Jugador }.Concat(sim.Companies.State.Companies.Select(c => c.Id)))
        {
            IReadOnlyList<TrialBalanceRow> rows = sim.Ledger.GetTrialBalance(entity);
            Assert.Equal(rows.Sum(r => r.Debits), rows.Sum(r => r.Credits));
        }
    }

    private static void Advance(EconomySimulation sim, EconomicDate target)
    {
        while (sim.CurrentDate < target)
        {
            long pocket = (long)sim.Ledger.GetBalance(LedgerEntities.Jugador, SystemAccounts.Caja)
                + (long)sim.Ledger.GetBalance(LedgerEntities.Jugador, SystemAccounts.DeudoresPorVentas);
            DayStartedInfo info = sim.OnDayStarted(sim.CurrentDate.AddDays(1), Weather.Sun, null, 10_000_000, pocket);
            sim.ReconcileCash(pocket - (long)info.CompanyTransfers - info.AutoDebitTotal, new EntryText("Conciliación"));
        }
    }

    /// <summary>Partida con una Minera S.A. que vendió cobre en primavera y ya cerró la estación.</summary>
    private static (EconomySimulation Sim, CompanyState Company) ProfitableSA(long sales = 242_000)
    {
        EconomySimulation sim = EconomySimulation.StartNewGame(TestData.WithoutInflation(), 1, Monday, "realista", 500_000);
        sim.OnDayStarted(Monday);
        CompanyResult r = sim.Companies.Create("minera", "SA", 30_000, unlocked: true, pocket: 500_000, Monday, new List<CompanyNotice>());
        Assert.Equal(CompanyError.None, r.Error);
        sim.RecordShipmentSales(new[] { new SoldItem(CopperOre, -15, 10_000, sales) });
        Advance(sim, new EconomicDate(1, Season.Summer, 2));
        return (sim, r.Company!);
    }

    // ───────────────────────────── Ticker y emisor ─────────────────────────────

    [Fact]
    public void SA_GetsATickerAndIsAPlayerIssuer()
    {
        (EconomySimulation sim, CompanyState minera) = ProfitableSA();
        Assert.Equal("MINA", minera.Ticker);
        Assert.True(sim.Credit.Issuers["MINA"].Player);
        Assert.Equal(minera.Rating, sim.Credit.Issuers["MINA"].Rating);
        Assert.Equal(0, sim.Credit.Recovery("MINA"));

        // Una S.R.L. no tiene ticker hasta transformarse.
        CompanyResult srl = sim.Companies.Create("pesquera", "SRL", 10_000, true, 1_000_000, sim.CurrentDate, new List<CompanyNotice>());
        Assert.Null(srl.Company!.Ticker);
        Assert.Equal(CompanyError.NotSA, sim.Companies.CanIpo(srl.Company, 0, 20_000));
    }

    // ───────────────────────────── ON ─────────────────────────────

    [Fact]
    public void IssueBond_ListsTheSeriesAndBooksTheDebt()
    {
        (EconomySimulation sim, CompanyState minera) = ProfitableSA();
        decimal limit = sim.Companies.BondLimit(minera);
        Assert.True(limit >= 10_000);
        Assert.Equal(CompanyError.AboveLimit, sim.Companies.CanIssueBond(minera, limit + 100, 4));
        Assert.Equal(CompanyError.InvalidTerm, sim.Companies.CanIssueBond(minera, 10_000, 3));

        decimal cashBefore = sim.Companies.Cash(minera);
        var notices = new List<CompanyNotice>();
        Assert.Equal(CompanyError.None, sim.Companies.IssueBond(minera.Id, 10_050, 4, 1, sim.CurrentDate, notices).Error);
        BondSeries series = Assert.Single(sim.Broker.ActiveBonds(sovereign: false), b => b.Issuer == "MINA");
        Assert.Equal("ONMINA1", series.Id);
        Assert.Equal(100, series.IssuedUnits); // se redondea a láminas de VN 100
        Assert.Equal(sim.Companies.BondCoupon(minera, 4), series.CouponRate, 6);
        Assert.Equal(10_000m, Balance(sim, minera.Id, SystemAccounts.OnEmitidas));
        Assert.Equal(cashBefore + 9_800m, sim.Companies.Cash(minera)); // 2% de colocación
        Assert.Contains(notices, n => n.Kind == CompanyNoticeKind.BondsIssued);
        AssertBalanced(sim);
    }

    [Fact]
    public void BondServices_ArePaidByTheCompany_AndThePlayerCollectsAsHolder()
    {
        (EconomySimulation sim, CompanyState minera) = ProfitableSA();
        sim.Companies.IssueBond(minera.Id, 10_000, 4, 1, sim.CurrentDate, new List<CompanyNotice>());
        sim.Broker.Open(sim.CurrentDate);
        sim.Broker.Deposit(5_000, sim.CurrentDate);
        var tuesday = new EconomicDate(1, Season.Summer, 2);
        Assert.Equal(TradeError.None, sim.Broker.Execute("ONMINA1", TradeSide.Buy, 10, tuesday, TenAm).Error); // las ON propias sí se compran

        Advance(sim, new EconomicDate(2, Season.Summer, 3)); // emitida en verano a 4 estaciones
        Assert.Empty(sim.Broker.ActiveBonds(false).Where(b => b.Issuer == "MINA"));
        Assert.Equal(0m, Balance(sim, minera.Id, SystemAccounts.OnEmitidas));
        Assert.True(Balance(sim, minera.Id, SystemAccounts.InteresesDeOn) > 0);
        Assert.True(Balance(sim, LedgerEntities.Jugador, SystemAccounts.InteresesGanados) > 0);
        Assert.True(minera.Active);
        AssertBalanced(sim);
    }

    [Fact]
    public void UnpaidBond_BankruptsTheCompany()
    {
        (EconomySimulation sim, CompanyState minera) = ProfitableSA();
        sim.Companies.IssueBond(minera.Id, 20_000, 4, 1, sim.CurrentDate, new List<CompanyNotice>());
        decimal cash = sim.Companies.Cash(minera);
        sim.Ledger.Post(minera.Id, sim.CurrentDate, "Retiro de prueba", EntrySources.Ajuste,
            JournalLine.ToDebit(SystemAccounts.EgresosNoClasificados, cash),
            JournalLine.ToCredit(SystemAccounts.Caja, cash));

        var notices = new List<CompanyNotice>();
        while (sim.CurrentDate < new EconomicDate(1, Season.Fall, 3))
        {
            notices.AddRange(sim.OnDayStarted(sim.CurrentDate.AddDays(1), Weather.Sun, null, 10_000_000, 10_000_000).CompanyNotices);
        }

        Assert.Contains(notices, n => n.Kind == CompanyNoticeKind.Bankrupt);
        Assert.False(minera.Active);
        Assert.True(sim.Credit.IsDefaulted("MINA"));
        Assert.Empty(sim.Broker.ActiveBonds(false).Where(b => b.Issuer == "MINA"));
        AssertBalanced(sim);
    }

    // ───────────────────────────── IPO ─────────────────────────────

    [Fact]
    public void Ipo_RequiresHistoryAndAValidFloat()
    {
        EconomySimulation sim = EconomySimulation.StartNewGame(TestData.WithoutInflation(), 1, Monday, "realista", 500_000);
        sim.OnDayStarted(Monday);
        CompanyState sa = sim.Companies.Create("minera", "SA", 30_000, true, 500_000, Monday, new List<CompanyNotice>()).Company!;
        Assert.Equal(CompanyError.NoHistory, sim.Companies.CanIpo(sa, 0, 20_000));

        (EconomySimulation sim2, CompanyState minera) = ProfitableSA();
        Assert.Equal(CompanyError.InvalidFloat, sim2.Companies.CanIpo(minera, 0, 5_000));   // 5% < 10%
        Assert.Equal(CompanyError.InvalidFloat, sim2.Companies.CanIpo(minera, 0, 60_000));  // 60% > 49%
        Assert.Equal(CompanyError.None, sim2.Companies.CanIpo(minera, 20_000, 10_000));    // 30.000 / 120.000 = 25%
    }

    [Fact]
    public void Ipo_PrimaryGoesToTheCompany_SecondaryToThePlayer_AndTheStockTrades()
    {
        (EconomySimulation sim, CompanyState minera) = ProfitableSA();
        decimal price = sim.Companies.IpoPrice(minera, 20_000);
        decimal fundamental = sim.Companies.FundamentalPrice(minera, 120_000);
        Assert.Equal(Math.Round(fundamental * 0.9m, 2), price);
        Assert.Equal((double)sim.Companies.TrailingAnnualProfit(minera) / 120_000, sim.Companies.MarketEps(minera, 120_000), 6); // UPA diluida

        double merzu = sim.Stocks.Merzu;
        decimal companyCash = sim.Companies.Cash(minera);
        decimal contributed = minera.Contributed;
        var notices = new List<CompanyNotice>();
        CompanyResult r = sim.Companies.Ipo(minera.Id, 20_000, 10_000, sim.CurrentDate, notices);
        Assert.Equal(CompanyError.None, r.Error);

        decimal primary = Math.Round(price * 20_000, 2);
        Assert.Equal(companyCash + primary - Math.Round(primary * 0.03m, 2), sim.Companies.Cash(minera));
        decimal secondary = Math.Round(price * 10_000, 2);
        Assert.Equal(secondary - Math.Round(secondary * 0.03m, 2), r.GoldDelta);
        Assert.Equal(120_000, minera.Listing!.TotalShares);
        Assert.Equal(90_000, minera.Listing.PlayerShares);
        Assert.Equal(contributed * 0.9m, minera.Contributed);
        Assert.Contains(notices, n => n.Kind == CompanyNoticeKind.Listed && n.Detail == "MINA");
        Assert.True(sim.Companies.MarketPrice(minera) >= price); // abre por encima de la colocación (no se cae por la dilución)

        // Cotiza en BYMZ, fuera del MERZU, y no se opera como especie.
        Assert.NotNull(sim.Stocks.Find("MINA"));
        Assert.Equal(merzu, sim.Stocks.Merzu, 6);
        sim.Broker.Open(sim.CurrentDate);
        sim.Broker.Deposit(10_000, sim.CurrentDate);
        Assert.Equal(TradeError.OwnCompany, sim.Broker.Quote("MINA", TradeSide.Buy, 1, new EconomicDate(1, Season.Summer, 2), TenAm).Error);

        // Al día siguiente sigue cotizando con su UPA real.
        Advance(sim, new EconomicDate(1, Season.Summer, 5));
        Assert.True(sim.Stocks.Get("MINA").Close > 0);
        Assert.Equal(sim.Companies.MarketEps(minera), sim.Stocks.Get("MINA").Eps, 6);
        AssertBalanced(sim);
    }

    [Fact]
    public void Dividends_GoPartlyToMinorityShareholders()
    {
        (EconomySimulation sim, CompanyState minera) = ProfitableSA();
        sim.Companies.Ipo(minera.Id, 0, 25_000, sim.CurrentDate, new List<CompanyNotice>()); // 25% en el público
        decimal cash = sim.Companies.Cash(minera);
        CompanyResult r = sim.Companies.PayDividend(minera.Id, 10_000, sim.CurrentDate);
        Assert.Equal(CompanyError.None, r.Error);
        Assert.Equal(cash - 10_000m, sim.Companies.Cash(minera)); // la sociedad paga todo
        Assert.Equal(7_500m * 0.93m, r.GoldDelta);                // a vos te toca el 75%, con retención
        AssertBalanced(sim);
    }

    [Fact]
    public void Buyback_ToHundredPercentDelists_AndFollowOnKeepsControl()
    {
        (EconomySimulation sim, CompanyState minera) = ProfitableSA();
        sim.Companies.Ipo(minera.Id, 0, 20_000, sim.CurrentDate, new List<CompanyNotice>());

        var notices = new List<CompanyNotice>();
        Assert.Equal(CompanyError.InvalidFloat, sim.Companies.FollowOn(minera.Id, 30_000, sim.CurrentDate, notices).Error); // 50% > 49%
        CompanyResult more = sim.Companies.FollowOn(minera.Id, 10_000, sim.CurrentDate, notices);
        Assert.Equal(CompanyError.None, more.Error);
        Assert.True(more.GoldDelta > 0);
        Assert.Equal(70_000, minera.Listing!.PlayerShares);

        decimal price = sim.Companies.MarketPrice(minera);
        CompanyResult back = sim.Companies.Buyback(minera.Id, 30_000, 10_000_000, sim.CurrentDate, notices);
        Assert.Equal(CompanyError.None, back.Error);
        Assert.Equal(-Math.Round(price * 1.15m * 30_000, 2), back.GoldDelta);
        Assert.Null(minera.Listing);
        Assert.Null(sim.Stocks.Find("MINA"));
        Assert.Contains(notices, n => n.Kind == CompanyNoticeKind.Delisted);
        AssertBalanced(sim);
    }

    [Fact]
    public void Listed_CompanyCannotBeDissolved()
    {
        (EconomySimulation sim, CompanyState minera) = ProfitableSA();
        sim.Companies.Ipo(minera.Id, 0, 20_000, sim.CurrentDate, new List<CompanyNotice>());
        Assert.Equal(CompanyError.NotAllowed, sim.Companies.Dissolve(minera.Id, sim.CurrentDate, new List<CompanyNotice>()).Error);
    }

    [Fact]
    public void SaveAndLoad_KeepsTheListingAndBonds()
    {
        (EconomySimulation sim, CompanyState minera) = ProfitableSA();
        sim.Companies.IssueBond(minera.Id, 5_000, 8, 4, sim.CurrentDate, new List<CompanyNotice>());
        sim.Companies.Ipo(minera.Id, 10_000, 10_000, sim.CurrentDate, new List<CompanyNotice>());

        EconomySimulation loaded = EconomySimulation.Load(TestData.WithoutInflation(), WorldStateSerializer.Deserialize(WorldStateSerializer.Serialize(sim.Snapshot())));
        CompanyState copy = loaded.Companies.Find(minera.Id)!;
        Assert.Equal("MINA", copy.Ticker);
        Assert.Equal(minera.Listing!.PlayerShares, copy.Listing!.PlayerShares);
        Assert.NotNull(loaded.Stocks.Find("MINA"));
        Assert.Equal(50, loaded.Broker.FindBond("ONMINA1")!.IssuedUnits);
        Assert.True(loaded.Credit.Issuers["MINA"].Player);
    }
}
