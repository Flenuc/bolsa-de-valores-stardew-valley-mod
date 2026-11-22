using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Ledger;
using ZuzuMarket.Core.Legal;
using ZuzuMarket.Core.Markets;
using ZuzuMarket.Core.Simulation;
using ZuzuMarket.Core.State;
using ZuzuMarket.Core.Taxation;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Tests.Legal;

public class LegalTests
{
    private const int TenAm = 1000;
    private static readonly EconomicDate Monday = new(1, Season.Spring, 1);

    private static EconomySimulation NewGame(string difficulty = "realista", long cash = 0, decimal deposit = 0)
    {
        EconomySimulation sim = EconomySimulation.StartNewGame(TestData.WithoutInflation(), 1, Monday, difficulty, cash);
        sim.OnDayStarted(Monday);
        if (deposit > 0)
        {
            sim.Broker.Open(Monday);
            sim.Broker.Deposit(deposit, Monday);
        }

        return sim;
    }

    private static decimal Balance(EconomySimulation sim, string account) => sim.Ledger.GetBalance(LedgerEntities.Jugador, account);

    private static void AssertBalanced(EconomySimulation sim)
    {
        IReadOnlyList<TrialBalanceRow> rows = sim.Ledger.GetTrialBalance(LedgerEntities.Jugador);
        Assert.Equal(rows.Sum(r => r.Debits), rows.Sum(r => r.Credits));
    }

    /// <summary>Avanza día por día y junta lo que pasó en lo legal.</summary>
    private static List<(EconomicDate Date, LegalDay Day)> Advance(EconomySimulation sim, EconomicDate target, long? budget = null)
    {
        var days = new List<(EconomicDate, LegalDay)>();
        while (sim.CurrentDate < target)
        {
            DayStartedInfo info = sim.OnDayStarted(sim.CurrentDate.AddDays(1), Weather.Sun, null, budget);
            days.Add((sim.CurrentDate, info.Legal));
        }

        return days;
    }

    private static EconomicDate FirstDay(List<(EconomicDate Date, LegalDay Day)> days, LegalNoticeKind kind) =>
        days.First(d => d.Day.Notices.Any(n => n.Kind == kind)).Date;

    // ───────────────────────────── Escalera ─────────────────────────────

    [Fact]
    public void Realista_FullLadder_EmbargoSeizureAndBankruptcy()
    {
        EconomySimulation sim = NewGame();
        sim.CreateTestDebt(50_000);
        List<(EconomicDate Date, LegalDay Day)> days = Advance(sim, new EconomicDate(1, Season.Summer, 5));

        // La deuda está en gestión desde el martes 2: embargo a los 7 días, secuestro a los 14, quiebra a los 28.
        Assert.Equal(new EconomicDate(1, Season.Spring, 9), FirstDay(days, LegalNoticeKind.EmbargoStarted));
        Assert.Equal(new EconomicDate(1, Season.Spring, 16), FirstDay(days, LegalNoticeKind.SeizureRequested));
        Assert.True(days.First(d => d.Date == new EconomicDate(1, Season.Spring, 16)).Day.SeizureRequest > 50_000);
        EconomicDate bankruptcy = FirstDay(days, LegalNoticeKind.BankruptcyDeclared);
        Assert.Equal(new EconomicDate(1, Season.Summer, 2), bankruptcy);
        Assert.Equal(new EconomicDate(1, Season.Summer, 3), FirstDay(days, LegalNoticeKind.BankruptcyClosed)); // se cierra solo al día siguiente

        Assert.Equal("D", sim.Legal.Rating);
        Assert.True(sim.Legal.IsInhabilitado(sim.CurrentDate));
        Assert.Equal(new EconomicDate(2, Season.Summer, 1), sim.Legal.State.InhabilitadoUntil); // 4 estaciones
        Assert.Equal(0m, sim.Legal.DebtInCollection);
        Assert.True(Balance(sim, SystemAccounts.QuitaConcursal) >= 50_000);
        AssertBalanced(sim);
    }

    [Fact]
    public void Argentina_IsFaster_AndCasual_NeverGoesBankrupt()
    {
        EconomySimulation argentina = NewGame("argentina");
        argentina.CreateTestDebt(10_000);
        List<(EconomicDate Date, LegalDay Day)> fast = Advance(argentina, new EconomicDate(1, Season.Summer, 1));
        Assert.Equal(new EconomicDate(1, Season.Spring, 7), FirstDay(fast, LegalNoticeKind.EmbargoStarted)); // 7 / 1,5 → 5 días
        Assert.Equal(new EconomicDate(1, Season.Spring, 21), FirstDay(fast, LegalNoticeKind.BankruptcyDeclared)); // 28 / 1,5 → 19

        EconomySimulation casual = NewGame("casual");
        casual.CreateTestDebt(10_000);
        List<(EconomicDate Date, LegalDay Day)> slow = Advance(casual, new EconomicDate(1, Season.Fall, 1));
        Assert.Contains(slow, d => d.Day.Notices.Any(n => n.Kind == LegalNoticeKind.EmbargoStarted));
        Assert.DoesNotContain(slow, d => d.Day.Notices.Any(n => n.Kind == LegalNoticeKind.BankruptcyDeclared));
        Assert.DoesNotContain(slow, d => d.Day.SeizureRequest > 0); // Casual solo embarga ingresos
        Assert.True(casual.Legal.IsEmbargoActive);
    }

    [Fact]
    public void AccountEmbargo_SeizesTheBrokerCashFreezesItAndInhibits()
    {
        EconomySimulation sim = NewGame(cash: 10_000, deposit: 5_000);
        sim.Broker.Execute("ZUEN", TradeSide.Buy, 10, Monday, TenAm);
        sim.CreateTestDebt(8_000);
        Advance(sim, new EconomicDate(1, Season.Spring, 9));

        Assert.True(sim.Legal.IsEmbargoActive);
        Assert.True(sim.Broker.Cash < 1); // el saldo libre se aplicó a la deuda
        Assert.Equal(TradeError.AccountFrozen, sim.Broker.Withdraw(1, sim.CurrentDate));
        var tuesday = new EconomicDate(1, Season.Spring, 9);
        Assert.Equal(TradeError.Inhibited, sim.Broker.Quote("JOJA", TradeSide.Buy, 1, tuesday, TenAm).Error);
        Assert.Equal(TradeError.None, sim.Broker.Quote("ZUEN", TradeSide.Sell, 10, tuesday, TenAm).Error); // vender sí se puede
        Assert.Equal(TradeError.Inhibited, sim.Legal.QuoteLoan(1_000, 4, sim.CurrentDate).Error);
        AssertBalanced(sim);
    }

    [Fact]
    public void IncomeEmbargo_TakesPartOfTheShippingBin_AndTheEmbargoIsLiftedWhenPaid()
    {
        EconomySimulation sim = NewGame();
        sim.CreateTestDebt(2_000);
        Advance(sim, new EconomicDate(1, Season.Spring, 9));
        Assert.True(sim.Legal.IsEmbargoActive);

        sim.RecordShipmentSales(new[] { new SoldItem("24", -75, 100, 10_000) });
        DayStartedInfo next = sim.OnDayStarted(new EconomicDate(1, Season.Spring, 10));
        Assert.True(next.Legal.Garnished > 0);
        Assert.True(next.Legal.Garnished <= 3_000);
        Assert.Contains(next.Legal.Notices, n => n.Kind == LegalNoticeKind.IncomeGarnished);

        // El oro del envío llega (menos lo embargado) y se concilia sin "no clasificados".
        sim.ReconcileCash((long)(10_000 - next.Legal.Garnished), "Conciliación");
        Assert.Equal(0m, Balance(sim, SystemAccounts.IngresosNoClasificados));

        // Pagando el resto se levanta el embargo.
        foreach (TaxObligation o in sim.Taxes.InCollection)
        {
            sim.Taxes.PayFrom(o.Id, o.Total, sim.CurrentDate, SystemAccounts.Caja);
        }

        DayStartedInfo after = sim.OnDayStarted(new EconomicDate(1, Season.Spring, 11));
        Assert.Contains(after.Legal.Notices, n => n.Kind == LegalNoticeKind.EmbargoLifted);
        Assert.False(sim.Legal.IsEmbargoActive);
        AssertBalanced(sim);
    }

    [Fact]
    public void Seizure_UsesTheSiloFirst_AndTheGameReportsTheRest()
    {
        EconomySimulation sim = NewGame(deposit: 0);
        sim.Broker.AddToSilo("276", 0, 100, 320); // 100 calabazas: valen 16.000 al 50%
        sim.CreateTestDebt(10_000);
        List<(EconomicDate Date, LegalDay Day)> days = Advance(sim, new EconomicDate(1, Season.Spring, 16));
        Assert.True(sim.Broker.SiloUnits("calabaza") < 100);
        Assert.True(Balance(sim, SystemAccounts.IngresosPorRemate) >= 10_000);

        decimal paid = sim.ApplySeizure(500);
        Assert.True(paid <= 500);
        AssertBalanced(sim);
    }

    // ───────────────────────────── Concurso ─────────────────────────────

    [Fact]
    public void Concurso_RestructuresTheDebtWithAHaircutAndInstallments()
    {
        EconomySimulation sim = NewGame();
        sim.CreateTestDebt(4_000);
        Advance(sim, new EconomicDate(1, Season.Spring, 9));
        Assert.True(sim.Legal.IsEmbargoActive);
        decimal debt = sim.Legal.DebtInCollection;

        LegalNotice? opened = sim.RequestConcurso();
        Assert.NotNull(opened);
        Assert.Equal(LegalNoticeKind.ConcursoOpened, opened!.Kind);
        Assert.False(sim.Legal.IsEmbargoActive);
        Assert.True(sim.Legal.IsInhibited);
        Assert.Null(sim.RequestConcurso()); // no se puede pedir dos veces

        var cuotas = sim.Taxes.Unpaid.Where(o => o.Kind == TaxKind.CuotaConcurso).ToList();
        Assert.Equal(4, cuotas.Count);
        decimal haircut = Math.Round(debt * 0.15m); // calificación BB
        Assert.InRange(cuotas.Sum(o => (decimal)o.Total), debt - haircut - 2, debt - haircut + 2);
        Assert.Equal(0m, sim.Legal.DebtInCollection);
        Assert.Equal(cuotas.Sum(o => (decimal)o.Total), Balance(sim, SystemAccounts.DeudaConcursal));

        // Pagando todas las cuotas con débito automático se termina el concurso.
        List<(EconomicDate Date, LegalDay Day)> days = Advance(sim, new EconomicDate(2, Season.Summer, 1), budget: 1_000_000);
        Assert.Contains(days, d => d.Day.Notices.Any(n => n.Kind == LegalNoticeKind.ConcursoCompleted));
        Assert.False(sim.Legal.State.ConcursoActive);
        Assert.Equal(0m, Balance(sim, SystemAccounts.DeudaConcursal));
        AssertBalanced(sim);
    }

    [Fact]
    public void Concurso_InDefault_EndsInBankruptcy()
    {
        EconomySimulation sim = NewGame();
        sim.CreateTestDebt(4_000);
        Advance(sim, new EconomicDate(1, Season.Spring, 9));
        sim.RequestConcurso();
        // Sin pagar nada: la primera cuota vence el 15 de verano, se intima el 22 y pasa a gestión el 2 de otoño.
        List<(EconomicDate Date, LegalDay Day)> days = Advance(sim, new EconomicDate(1, Season.Fall, 10));
        Assert.Contains(days, d => d.Day.Notices.Any(n => n.Kind == LegalNoticeKind.BankruptcyDeclared));
        Assert.False(sim.Legal.State.ConcursoActive);
        Assert.Equal(0m, Balance(sim, SystemAccounts.DeudaConcursal));
        AssertBalanced(sim);
    }

    // ───────────────────────────── Quiebra ─────────────────────────────

    [Fact]
    public void Bankruptcy_LiquidatesThePortfolio_AndRehabilitatesLater()
    {
        EconomySimulation sim = NewGame(cash: 100_000, deposit: 20_000);
        sim.Broker.Execute("JOJA", TradeSide.Buy, 50, Monday, TenAm);
        sim.Broker.Execute("BOFE1", TradeSide.Buy, 20, Monday, TenAm);
        sim.Broker.PlaceCaucion(1_000, 28, Monday, TenAm);
        sim.CreateTestDebt(500_000);

        List<(EconomicDate Date, LegalDay Day)> ladder = Advance(sim, new EconomicDate(1, Season.Summer, 3));
        Assert.True(ladder.Sum(d => d.Day.CashSeized) > 70_000); // el bolsillo (80.000) va a los acreedores…
        Assert.Equal(500m, Balance(sim, SystemAccounts.Caja)); // …salvo el mínimo inembargable
        Assert.Equal(0d, sim.Legal.State.Score);
        Assert.Contains(ladder, d => d.Day.Notices.Any(n => n.Kind == LegalNoticeKind.CashSeized));
        Assert.Empty(sim.Broker.State.Stocks);
        Assert.Empty(sim.Broker.State.Bonds);
        Assert.Empty(sim.Broker.State.Cauciones);
        Assert.Equal(0m, Balance(sim, SystemAccounts.Acciones));
        Assert.Equal(0m, sim.Legal.DebtInCollection);
        Assert.Equal(1, sim.Legal.State.Bankruptcies);
        Assert.Equal(TradeError.Inhibited, sim.Broker.Quote("JOJA", TradeSide.Buy, 1, new EconomicDate(1, Season.Summer, 3), TenAm).Error);

        List<(EconomicDate Date, LegalDay Day)> later = Advance(sim, new EconomicDate(2, Season.Summer, 5), budget: 10_000_000);
        Assert.Contains(later, d => d.Day.Notices.Any(n => n.Kind == LegalNoticeKind.Rehabilitated));
        Assert.False(sim.Legal.IsInhibited);
        AssertBalanced(sim);
    }

    // ───────────────────────────── Préstamos y caución tomadora ─────────────────────────────

    [Fact]
    public void Loan_FrenchInstallmentsBilledEachSeason()
    {
        EconomySimulation sim = NewGame(cash: 1_000);
        sim.RecordShopSales(new[] { new SoldItem("24", -75, 1, 100_000) }); // facturación para tener límite
        LoanQuote q = sim.Legal.QuoteLoan(10_000, 4, Monday);
        Assert.Equal(TradeError.None, q.Error);
        Assert.Equal(LegalEngine.FrenchInstallment(10_000, q.Rate, 4), q.Installment);
        Assert.Equal(TradeError.AboveLimit, sim.Legal.QuoteLoan(q.Limit + 1, 4, Monday).Error);
        Assert.Equal(TradeError.InvalidTerm, sim.Legal.QuoteLoan(1_000, 3, Monday).Error);

        var notices = new List<LegalNotice>();
        sim.Legal.TakeLoan(10_000, 4, Monday, notices);
        Assert.Equal(10_000m, Balance(sim, SystemAccounts.PrestamosBancarios));
        Assert.Equal(111_000m, Balance(sim, SystemAccounts.Caja));

        Advance(sim, new EconomicDate(2, Season.Spring, 20), budget: 1_000_000);
        var cuotas = sim.Taxes.State.Obligations.Where(o => o.Kind == TaxKind.CuotaPrestamo).ToList();
        Assert.Equal(4, cuotas.Count);
        Assert.All(cuotas, o => Assert.True(o.IsPaid));
        Assert.All(cuotas.Take(3), o => Assert.Equal(q.Installment, o.PaidAmount));
        Assert.Equal(0m, Balance(sim, SystemAccounts.PrestamosBancarios));
        Assert.Empty(sim.Legal.State.Loans);
        AssertBalanced(sim);
    }

    [Fact]
    public void Repo_IsLimitedByTheSecurities_AndForcesSalesIfUnpaid()
    {
        EconomySimulation sim = NewGame(cash: 50_000, deposit: 20_000);
        sim.Broker.Execute("BZUZ", TradeSide.Buy, 100, Monday, TenAm);
        decimal limit = sim.Broker.RepoLimit(Monday, TenAm);
        Assert.InRange(limit, sim.Broker.SecuritiesValue(Monday, TenAm) * 0.49m, sim.Broker.SecuritiesValue(Monday, TenAm) * 0.5m);
        Assert.Equal(TradeError.AboveLimit, sim.Broker.BorrowRepo(limit + 10, 7, Monday, TenAm));
        Assert.Equal(TradeError.None, sim.Broker.BorrowRepo(3_000, 7, Monday, TenAm));
        Assert.Equal(3_000m, Balance(sim, SystemAccounts.CaucionTomadora));

        sim.Broker.Withdraw(sim.Broker.Cash, Monday); // se lleva todo: al vencer no alcanza
        DayStartedInfo maturity = sim.OnDayStarted(new EconomicDate(1, Season.Spring, 8), Weather.Sun, null, 1_000_000);
        Assert.Contains(maturity.MarketNotices, n => n.Kind == MarketNoticeKind.RepoForcedSale);
        Assert.Contains(maturity.MarketNotices, n => n.Kind == MarketNoticeKind.RepoRepaid);
        Assert.Equal(0m, Balance(sim, SystemAccounts.CaucionTomadora));
        Assert.True(sim.Broker.State.Stocks.GetValueOrDefault("BZUZ")?.Quantity is null or < 100);
        AssertBalanced(sim);
    }

    // ───────────────────────────── Calificación ─────────────────────────────

    [Fact]
    public void Rating_ImprovesWithAGoodRecord_AndWorsensWithLatePayments()
    {
        EconomySimulation good = NewGame(cash: 300_000);
        good.RecordShopSales(new[] { new SoldItem("24", -75, 1, 200_000) });
        Advance(good, new EconomicDate(1, Season.Fall, 2), budget: 10_000_000);
        int goodIndex = good.Data.Ratings.IndexOf(good.Legal.Rating);

        EconomySimulation bad = NewGame(cash: 0);
        Advance(bad, new EconomicDate(1, Season.Fall, 2)); // no paga nada: atrasos, intimaciones, embargo
        int badIndex = bad.Data.Ratings.IndexOf(bad.Legal.Rating);

        Assert.True(goodIndex < bad.Data.Ratings.IndexOf("BB"), $"Buena conducta: {good.Legal.Rating}");
        Assert.True(badIndex > goodIndex, $"Mala conducta: {bad.Legal.Rating}");
    }

    [Fact]
    public void SaveAndLoad_KeepsLoansAndLegalSituation()
    {
        EconomySimulation sim = NewGame(cash: 1_000);
        sim.RecordShopSales(new[] { new SoldItem("24", -75, 1, 100_000) });
        sim.Legal.TakeLoan(5_000, 2, Monday, new List<LegalNotice>());
        sim.CreateTestDebt(1_000);
        Advance(sim, new EconomicDate(1, Season.Spring, 10));

        EconomySimulation loaded = EconomySimulation.Load(TestData.WithoutInflation(), WorldStateSerializer.Deserialize(WorldStateSerializer.Serialize(sim.Snapshot())));
        Assert.Single(loaded.Legal.State.Loans);
        Assert.Equal(sim.Legal.IsEmbargoActive, loaded.Legal.IsEmbargoActive);
        Assert.Equal(sim.Legal.Rating, loaded.Legal.Rating);
        Assert.Equal(sim.Legal.State.CollectionSince, loaded.Legal.State.CollectionSince);
    }
}
