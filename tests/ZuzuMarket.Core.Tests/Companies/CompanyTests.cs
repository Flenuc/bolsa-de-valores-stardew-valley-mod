using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Companies;
using ZuzuMarket.Core.Ledger;
using ZuzuMarket.Core.Legal;
using ZuzuMarket.Core.Simulation;
using ZuzuMarket.Core.State;
using ZuzuMarket.Core.Taxation;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Tests.Companies;

public class CompanyTests
{
    private static readonly EconomicDate Monday = new(1, Season.Spring, 1);
    private const string CopperOre = "378";

    private static EconomySimulation NewGame(long cash = 100_000, string difficulty = "realista")
    {
        EconomySimulation sim = EconomySimulation.StartNewGame(TestData.WithoutInflation(), 1, Monday, difficulty, cash);
        sim.OnDayStarted(Monday);
        return sim;
    }

    private static decimal Balance(EconomySimulation sim, string entity, string account) => sim.Ledger.GetBalance(entity, account);

    private static void AssertBalanced(EconomySimulation sim)
    {
        foreach (string entity in new[] { LedgerEntities.Jugador }.Concat(sim.Companies.State.Companies.Select(c => c.Id)))
        {
            IReadOnlyList<TrialBalanceRow> rows = sim.Ledger.GetTrialBalance(entity);
            Assert.Equal(rows.Sum(r => r.Debits), rows.Sum(r => r.Credits));
        }
    }

    private static CompanyState Create(EconomySimulation sim, string type = "minera", string form = "SRL", decimal capital = 10_000)
    {
        CompanyResult r = sim.Companies.Create(type, form, capital, unlocked: true, pocket: sim.Ledger.GetBalance(LedgerEntities.Jugador, SystemAccounts.Caja), sim.CurrentDate, new List<CompanyNotice>());
        Assert.Equal(CompanyError.None, r.Error);
        return r.Company!;
    }

    /// <summary>Avanza días simulando que el juego descuenta del bolsillo lo que corresponde y concilia.</summary>
    private static void Advance(EconomySimulation sim, EconomicDate target, long budget = 10_000_000)
    {
        while (sim.CurrentDate < target)
        {
            long pocket = (long)sim.Ledger.GetBalance(LedgerEntities.Jugador, SystemAccounts.Caja)
                + (long)sim.Ledger.GetBalance(LedgerEntities.Jugador, SystemAccounts.DeudoresPorVentas);
            DayStartedInfo info = sim.OnDayStarted(sim.CurrentDate.AddDays(1), Weather.Sun, null, budget, pocket);
            long gold = pocket - (long)info.CompanyTransfers - info.AutoDebitTotal;
            sim.ReconcileCash(gold, new EntryText("Conciliación"));
        }
    }

    // ───────────────────────────── Alta ─────────────────────────────

    [Fact]
    public void Create_ValidatesAndBooksTheCapitalInBothLedgers()
    {
        EconomySimulation sim = NewGame();
        PlayerCompanies companies = sim.Companies;
        Assert.Equal(CompanyError.Locked, companies.CanCreate("minera", "SRL", 10_000, unlocked: false, pocket: 100_000));
        Assert.Equal(CompanyError.BelowMinimumCapital, companies.CanCreate("minera", "SRL", 1_000, true, 100_000));
        Assert.Equal(CompanyError.InsufficientFunds, companies.CanCreate("minera", "SRL", 10_000, true, 10_200));
        Assert.Equal(CompanyError.UnknownType, companies.CanCreate("banco", "SRL", 10_000, true, 100_000));

        CompanyState minera = Create(sim);
        Assert.Equal(CompanyError.AlreadyExists, companies.CanCreate("minera", "SRL", 10_000, true, 100_000));
        Assert.Equal(10_000m, Balance(sim, minera.Id, SystemAccounts.Caja));
        Assert.Equal(10_000m, Balance(sim, minera.Id, SystemAccounts.Capital));
        Assert.Equal(10_000m, Balance(sim, LedgerEntities.Jugador, SystemAccounts.ParticipacionesEnSociedades));
        Assert.Equal(100_000m - 10_000m - 500m, Balance(sim, LedgerEntities.Jugador, SystemAccounts.Caja));
        Assert.Equal(TaxRegime.ResponsableInscripto, companies.TaxesOf(minera).Regime);
        Assert.StartsWith("30-", minera.Cuit);
        AssertBalanced(sim);
    }

    [Fact]
    public void Inhibited_PlayerCannotCreateCompanies()
    {
        EconomySimulation sim = NewGame(cash: 0);
        sim.CreateTestDebt(5_000);
        Advance(sim, new EconomicDate(1, Season.Spring, 10), budget: 0);
        Assert.True(sim.Legal.IsInhibited);
        Assert.Equal(CompanyError.Inhibited, sim.Companies.CanCreate("agropecuaria", "SRL", 10_000, true, 100_000));
    }

    // ───────────────────────────── Ventas ─────────────────────────────

    [Fact]
    public void Sales_OfTheCompanyActivityGoToItsLedger_AndTheGoldIsTransferredNextDay()
    {
        EconomySimulation sim = NewGame();
        CompanyState minera = Create(sim);
        sim.RecordShipmentSales(new[] { new SoldItem(CopperOre, -15, 500, 5_000), new SoldItem("24", -75, 100, 3_500) });

        Assert.Equal(5_000m, Balance(sim, minera.Id, SystemAccounts.CobrosARendirPorSocio));
        Assert.Equal(5_000m, Balance(sim, LedgerEntities.Jugador, SystemAccounts.CobranzasARendirASociedades));
        Assert.Equal(3_500m, sim.Taxes.State.Current.NetSales); // la chirivía sigue siendo del jugador
        decimal iva = Balance(sim, minera.Id, SystemAccounts.IvaDebitoFiscal);
        Assert.InRange(iva, 867m, 868m); // 21% incluido en 5.000

        Advance(sim, new EconomicDate(1, Season.Spring, 2));
        Assert.Equal(0m, Balance(sim, minera.Id, SystemAccounts.CobrosARendirPorSocio));
        Assert.Equal(15_000m, Balance(sim, minera.Id, SystemAccounts.Caja));
        Assert.Equal(0m, Balance(sim, LedgerEntities.Jugador, SystemAccounts.IngresosNoClasificados));
        AssertBalanced(sim);
    }

    [Fact]
    public void SellMultiplier_IncludesFasAndTheCompanyBonus()
    {
        EconomySimulation sim = NewGame();
        double before = sim.SellMultiplier(CopperOre, -15);
        double market = sim.Market.GetMultiplierForItem(CopperOre);
        Assert.Equal(market * (1 - (0.08 * 0.5)), before, 6); // retención 8%, el acopiador traslada la mitad
        Create(sim);
        Assert.Equal(before * 1.08, sim.SellMultiplier(CopperOre, -15), 6);
        Assert.Equal(sim.Market.GetMultiplierForItem("74") * 1.08, sim.SellMultiplier("74", -2), 6); // prisma: gema, sin FAS pero de la Minera
        Assert.Equal(sim.Market.GetMultiplierForItem("128"), sim.SellMultiplier("128", -4), 6); // pez globo: no es commodity, sin Pesquera
    }

    [Fact]
    public void PurchaseMultiplier_AddsImportDutiesOnlyInImportShops()
    {
        EconomySimulation sim = NewGame();
        Assert.Equal(sim.Inflation.Index, sim.PurchaseMultiplier("SeedShop"), 6);
        Assert.Equal(sim.Inflation.Index * 1.23, sim.PurchaseMultiplier("Sandy"), 6);
    }

    // ───────────────────────────── Impuestos ─────────────────────────────

    [Fact]
    public void CompanyTaxes_AreIssuedAndAutoPaidFromItsCash()
    {
        EconomySimulation sim = NewGame();
        CompanyState minera = Create(sim, capital: 20_000);
        sim.RecordShipmentSales(new[] { new SoldItem(CopperOre, -15, 5_000, 60_500) });
        Advance(sim, new EconomicDate(1, Season.Summer, 20));

        List<TaxObligation> obligations = sim.Companies.TaxesOf(minera).State.Obligations;
        Assert.Contains(obligations, o => o.Kind == TaxKind.Iva && o.Period == new FiscalPeriod(1, Season.Spring));
        Assert.Contains(obligations, o => o.Kind == TaxKind.IngresosBrutos);
        Assert.Contains(obligations, o => o.Kind == TaxKind.TasaFiscalizacion);
        Assert.DoesNotContain(obligations, o => o.Kind is TaxKind.Monotributo or TaxKind.Abl);
        Assert.All(obligations, o => Assert.True(o.IsPaid));
        Assert.DoesNotContain(sim.Taxes.State.Obligations, o => obligations.Any(c => c.Id == o.Id)); // ids únicos
        Assert.Equal(sim.Companies.TaxesOf(minera).Find(obligations[0].Id), sim.FindObligation(obligations[0].Id)!.Value.Obligation);
        AssertBalanced(sim);
    }

    [Fact]
    public void CompanyGanancias_UsesTheCompanyScale()
    {
        EconomySimulation sim = NewGame();
        CompanyState minera = Create(sim, capital: 50_000);
        sim.RecordShipmentSales(new[] { new SoldItem(CopperOre, -15, 10_000, 121_000) }); // neto 100.000
        Advance(sim, new EconomicDate(2, Season.Spring, 2));

        TaxObligation ganancias = sim.Companies.TaxesOf(minera).State.Obligations.Single(o => o.Kind == TaxKind.Ganancias);
        // Imponible = 100.000 − IIBB 3.000 − SyH (600 + 3 × 100) − tasas de fiscalización 3 × 100 = 95.800 → 25%.
        Assert.Equal(23_950, ganancias.Issued);
        AssertBalanced(sim);
    }

    // ───────────────────────────── Socio ─────────────────────────────

    [Fact]
    public void Dividends_AreLimitedByRetainedEarnings_AndWithheld()
    {
        EconomySimulation sim = NewGame();
        CompanyState minera = Create(sim);
        Assert.Equal(0m, sim.Companies.Distributable(minera)); // el capital no se distribuye
        Assert.Equal(CompanyError.NothingToDistribute, sim.Companies.PayDividend(minera.Id, 1, sim.CurrentDate).Error);

        sim.RecordShipmentSales(new[] { new SoldItem(CopperOre, -15, 2_000, 12_100) });
        Advance(sim, new EconomicDate(1, Season.Spring, 3));
        decimal distributable = sim.Companies.Distributable(minera);
        Assert.InRange(distributable, 9_000m, 10_000m); // ventas netas (sin IVA) menos lo devengado

        CompanyResult paid = sim.Companies.PayDividend(minera.Id, 5_000, sim.CurrentDate);
        Assert.Equal(CompanyError.None, paid.Error);
        Assert.Equal(4_650m, paid.GoldDelta); // 7% de retención
        Assert.Equal(5_000m, Balance(sim, LedgerEntities.Jugador, SystemAccounts.Dividendos));
        AssertBalanced(sim);
    }

    [Fact]
    public void Transform_ToSA_ToppingUpTheCapital()
    {
        EconomySimulation sim = NewGame();
        CompanyState minera = Create(sim, capital: 6_000);
        CompanyResult r = sim.Companies.Transform(minera.Id, "SA", 100_000, sim.CurrentDate, new List<CompanyNotice>());
        Assert.Equal(CompanyError.None, r.Error);
        Assert.Equal(-(14_000m + 1_000m), r.GoldDelta);
        Assert.Equal("SA", minera.Form);
        Assert.Equal(20_000m, sim.Companies.CapitalOf(minera));
        Assert.Equal(20_000m, minera.Contributed);
        AssertBalanced(sim);
    }

    [Fact]
    public void Dissolve_PaysDebtsAndReturnsTheRemainder()
    {
        EconomySimulation sim = NewGame();
        CompanyState minera = Create(sim);
        sim.RecordShipmentSales(new[] { new SoldItem(CopperOre, -15, 2_000, 12_100) });
        Advance(sim, new EconomicDate(1, Season.Spring, 3));

        var notices = new List<CompanyNotice>();
        CompanyResult r = sim.Companies.Dissolve(minera.Id, sim.CurrentDate, notices);
        Assert.Equal(CompanyError.None, r.Error);
        Assert.InRange(r.GoldDelta, 20_000m, 22_100m); // capital + ventas − IVA del período
        Assert.False(minera.Active);
        Assert.Equal(0m, Balance(sim, LedgerEntities.Jugador, SystemAccounts.ParticipacionesEnSociedades));
        Assert.Equal(0m, sim.Companies.Equity(minera));
        Assert.Contains(notices, n => n.Kind == CompanyNoticeKind.Dissolved);

        // Ya no absorbe ventas.
        sim.RecordShipmentSales(new[] { new SoldItem(CopperOre, -15, 100, 600) });
        Assert.Equal(600m, sim.Taxes.State.Current.NetSales);
        AssertBalanced(sim);
    }

    [Fact]
    public void Insolvency_BankruptsTheCompanyWithoutTouchingThePlayer()
    {
        EconomySimulation sim = NewGame(cash: 200_000);
        CompanyState minera = Create(sim, capital: 5_000);
        sim.RecordShipmentSales(new[] { new SoldItem(CopperOre, -15, 10_000, 121_000) });
        Advance(sim, new EconomicDate(1, Season.Spring, 2));
        sim.Companies.PayDividend(minera.Id, sim.Companies.Distributable(minera), sim.CurrentDate); // se lleva todo

        // Le saco la caja para que no pueda pagar el IVA de primavera.
        decimal cash = sim.Companies.Cash(minera);
        sim.Ledger.Post(minera.Id, sim.CurrentDate, "Retiro de prueba", EntrySources.Ajuste,
            JournalLine.ToDebit(SystemAccounts.EgresosNoClasificados, cash),
            JournalLine.ToCredit(SystemAccounts.Caja, cash));

        decimal playerCash = Balance(sim, LedgerEntities.Jugador, SystemAccounts.Caja);
        var notices = new List<CompanyNotice>();
        // Pasa a gestión de cobro el 2 de otoño: quiebra 28 días después.
        while (sim.CurrentDate < new EconomicDate(1, Season.Winter, 5))
        {
            DayStartedInfo info = sim.OnDayStarted(sim.CurrentDate.AddDays(1), Weather.Sun, null, 10_000_000, 10_000_000);
            notices.AddRange(info.CompanyNotices);
        }

        Assert.Contains(notices, n => n.Kind == CompanyNoticeKind.InCollection);
        Assert.Contains(notices, n => n.Kind == CompanyNoticeKind.Bankrupt);
        Assert.False(minera.Active);
        Assert.True(minera.Bankrupt);
        Assert.Equal(0m, sim.Legal.DebtInCollection); // el jugador no debe nada
        Assert.False(sim.Legal.IsEmbargoActive);
        Assert.Contains(sim.Legal.State.Events, e => e.Kind == LegalEventKind.CompanyBankruptcy);
        Assert.Equal(0m, Balance(sim, LedgerEntities.Jugador, SystemAccounts.ParticipacionesEnSociedades));
        Assert.True(Balance(sim, LedgerEntities.Jugador, SystemAccounts.Caja) <= playerCash); // a lo sumo pagó sus propias boletas
        AssertBalanced(sim);
    }

    [Fact]
    public void PayingACompanyBillAtLewis_UsesItsCashAndThePocketForTheRest()
    {
        EconomySimulation sim = NewGame();
        CompanyState minera = Create(sim, capital: 5_000);
        TaxEngine taxes = sim.Companies.TaxesOf(minera);
        TaxObligation fee = taxes.IssueFee(TaxKind.TasaFiscalizacion, 8_000, sim.CurrentDate, SystemAccounts.TasasOtras, new List<TaxNotice>())!;

        long? fromPocket = sim.PayObligation(fee.Id);
        Assert.Equal(3_000, fromPocket);
        Assert.True(fee.IsPaid);
        Assert.Equal(0m, sim.Companies.Cash(minera));
        Assert.Equal(8_000m, minera.Contributed);
        AssertBalanced(sim);
    }

    // ───────────────────────────── Exportación ─────────────────────────────

    [Fact]
    public void Export_PaysDutiesAndIsCollectedAfterSevenDays()
    {
        EconomySimulation sim = NewGame();
        Assert.Null(sim.Companies.Exporter);
        CompanyState exporter = Create(sim, "comercializadora");
        sim.Companies.AddToContainer("262", 0, 1_000, 25); // 1.000 de trigo

        double multiplier = sim.Market.GetMultiplierForItem("262");
        var notices = new List<CompanyNotice>();
        DayStartedInfo info = sim.OnDayStarted(new EconomicDate(1, Season.Spring, 2), Weather.Sun, null, 10_000_000, 10_000_000);
        CompanyNotice exported = Assert.Single(info.CompanyNotices, n => n.Kind == CompanyNoticeKind.Exported);
        Assert.Empty(sim.Companies.State.ExportContainer);

        decimal fob = Balance(sim, exporter.Id, SystemAccounts.Ventas);
        decimal duty = Balance(sim, exporter.Id, SystemAccounts.DerechosDeExportacion);
        Assert.InRange(fob, 25_000m * (decimal)multiplier * 1.10m, 25_000m * (decimal)multiplier * 1.30m);
        Assert.Equal(Math.Round(fob * 0.12m, 2), duty);
        Assert.Equal(0m, Balance(sim, exporter.Id, SystemAccounts.IvaDebitoFiscal)); // exportación sin IVA
        Assert.Equal(fob - duty, Balance(sim, exporter.Id, SystemAccounts.ExportacionesACobrar));

        while (sim.CurrentDate < new EconomicDate(1, Season.Spring, 10))
        {
            notices.AddRange(sim.OnDayStarted(sim.CurrentDate.AddDays(1), Weather.Sun, null, 10_000_000, 10_000_000).CompanyNotices);
        }

        Assert.Contains(notices, n => n.Kind == CompanyNoticeKind.ExportCollected);
        Assert.Equal(0m, Balance(sim, exporter.Id, SystemAccounts.ExportacionesACobrar));
        AssertBalanced(sim);
    }

    [Fact]
    public void SaveAndLoad_KeepsCompaniesAndTheirLedgers()
    {
        EconomySimulation sim = NewGame();
        CompanyState minera = Create(sim);
        sim.Companies.AddToContainer("262", 0, 10, 25);
        sim.RecordShipmentSales(new[] { new SoldItem(CopperOre, -15, 100, 1_210) });

        EconomySimulation loaded = EconomySimulation.Load(TestData.WithoutInflation(), WorldStateSerializer.Deserialize(WorldStateSerializer.Serialize(sim.Snapshot())));
        CompanyState copy = Assert.Single(loaded.Companies.Active);
        Assert.Equal(minera.Cuit, copy.Cuit);
        Assert.Equal(sim.Companies.Cash(minera), loaded.Companies.Cash(copy));
        Assert.Equal(1_210m, loaded.Ledger.GetBalance(copy.Id, SystemAccounts.CobrosARendirPorSocio));
        Assert.Single(loaded.Companies.State.ExportContainer);
        Assert.Equal(TaxRegime.ResponsableInscripto, loaded.Companies.TaxesOf(copy).Regime);
    }
}
