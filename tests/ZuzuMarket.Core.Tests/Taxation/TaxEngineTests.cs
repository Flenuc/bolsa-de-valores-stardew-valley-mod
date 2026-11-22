using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Ledger;
using ZuzuMarket.Core.Simulation;
using ZuzuMarket.Core.State;
using ZuzuMarket.Core.Taxation;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Tests.Taxation;

public class TaxEngineTests
{
    private static readonly EconomicDate Spring1 = new(1, Season.Spring, 1);
    private static readonly FarmSnapshot FarmWithCoop = new(0, new[] { "Farmhouse", "Shipping Bin", "Coop" });

    /// <summary>Partida sin eventos ni inflación, ya dada de alta en el fisco.</summary>
    private static EconomySimulation NewGame(string difficulty = "realista", long cash = 10_000, FarmSnapshot? farm = null)
    {
        EconomySimulation sim = EconomySimulation.StartNewGame(TestData.WithoutInflation(), 1, Spring1, difficulty, cash);
        sim.OnDayStarted(Spring1, Weather.Sun, farm ?? FarmSnapshot.Empty);
        return sim;
    }

    private static decimal Balance(EconomySimulation sim, string account) => sim.Ledger.GetBalance(LedgerEntities.Jugador, account);

    private static void AssertBalanced(EconomySimulation sim)
    {
        IReadOnlyList<TrialBalanceRow> rows = sim.Ledger.GetTrialBalance(LedgerEntities.Jugador);
        Assert.Equal(rows.Sum(r => r.Debits), rows.Sum(r => r.Credits));
    }

    private static TaxObligation Single(EconomySimulation sim, TaxKind kind, FiscalPeriod period) =>
        Assert.Single(sim.Taxes.State.Obligations, o => o.Kind == kind && o.Period == period);

    [Fact]
    public void Start_RegistersAsMonotributistaAndIssuesFeeAndAbl()
    {
        EconomySimulation sim = EconomySimulation.StartNewGame(TestData.WithoutInflation(), 1, Spring1, "realista", 500);
        DayStartedInfo info = sim.OnDayStarted(Spring1, Weather.Sun, FarmWithCoop);

        Assert.True(sim.Taxes.Started);
        Assert.Equal(TaxRegime.Monotributo, sim.Taxes.Regime);
        Assert.Equal("A", sim.Taxes.Category);
        Assert.Contains(info.TaxNotices, n => n.Kind == TaxNoticeKind.Registered);
        Assert.Equal(2, info.TaxNotices.Count(n => n.Kind == TaxNoticeKind.Issued));

        TaxObligation fee = Single(sim, TaxKind.Monotributo, Spring1.FiscalPeriod);
        TaxObligation abl = Single(sim, TaxKind.Abl, Spring1.FiscalPeriod);
        Assert.Equal(150, fee.Principal);
        Assert.Equal(150 + 50, abl.Principal); // casa nivel 0 + gallinero
        Assert.Equal(Jurisdiction.Municipalidad, abl.Jurisdiction);

        // El 14 de primavera es domingo: vence el lunes 15.
        Assert.Equal(new EconomicDate(1, Season.Spring, 15), fee.DueDate);
        Assert.Equal(150m, Balance(sim, SystemAccounts.ImpuestosAPagarArf));
        Assert.Equal(200m, Balance(sim, SystemAccounts.TasasAPagarMunicipalidad));
        AssertBalanced(sim);

        // No se da de alta dos veces.
        sim.OnDayStarted(Spring1, Weather.Sun, FarmWithCoop);
        Assert.Equal(2, sim.Taxes.State.Obligations.Count);
    }

    [Fact]
    public void TaxBurden_ScalesAmountsByDifficulty()
    {
        EconomySimulation casual = NewGame("casual");
        EconomySimulation argentina = NewGame("argentina");
        Assert.Equal(75, Single(casual, TaxKind.Monotributo, Spring1.FiscalPeriod).Principal);
        Assert.Equal(180, Single(argentina, TaxKind.Monotributo, Spring1.FiscalPeriod).Principal);
    }

    [Fact]
    public void ShippingSale_GoesToReceivableAndIsCollectedWhenTheGoldArrives()
    {
        EconomySimulation sim = NewGame(cash: 500);
        sim.RecordShipmentSales(new[] { new SoldItem("24", -75, 30, 1000) });

        Assert.Equal(1000m, Balance(sim, SystemAccounts.DeudoresPorVentas));
        Assert.Equal(1000m, Balance(sim, SystemAccounts.Ventas));
        Assert.Equal(0m, Balance(sim, SystemAccounts.IvaDebitoFiscal));
        Assert.Equal(30, sim.Market.Get("chirivia").PendingSales);

        sim.ReconcileCash(1500, "Conciliación");
        Assert.Equal(0m, Balance(sim, SystemAccounts.DeudoresPorVentas));
        Assert.Equal(1500m, Balance(sim, SystemAccounts.Caja));
        Assert.Equal(0m, Balance(sim, SystemAccounts.IngresosNoClasificados));
        AssertBalanced(sim);
    }

    [Fact]
    public void ShopSalesAndPurchases_AreClassifiedAndActivitiesAreSeparated()
    {
        EconomySimulation sim = NewGame(cash: 1000);
        sim.RecordShopSales(new[]
        {
            new SoldItem("24", -75, 10, 300),   // chirivía → agro
            new SoldItem("378", -15, 50, 250),  // cobre → minería
            new SoldItem("390", -16, 99, 99),   // piedra → minería por categoría
            new SoldItem("999", -24, 1, 100),   // decoración → otras
        });
        sim.RecordPurchase(400);

        Assert.Equal(1000m + 749m - 400m, Balance(sim, SystemAccounts.Caja));
        Assert.Equal(400m, Balance(sim, SystemAccounts.ComprasEnTiendas));
        PeriodAccumulator current = sim.Taxes.State.Current;
        Assert.Equal(300m, current.NetSalesByActivity["agro"]);
        Assert.Equal(349m, current.NetSalesByActivity["mineria"]);
        Assert.Equal(100m, current.NetSalesByActivity["otros"]);
        Assert.Equal(50, sim.Market.Get("cobre").PendingSales);
        AssertBalanced(sim);
    }

    [Fact]
    public void NextPeriod_DeclaresIngresosBrutosAndSeguridadHigiene()
    {
        EconomySimulation sim = NewGame();
        sim.RecordShopSales(new[] { new SoldItem("24", -75, 1, 10_000), new SoldItem("378", -15, 1, 2_000) });
        TestData.AdvanceTo(sim, new EconomicDate(1, Season.Summer, 1));

        var spring = new FiscalPeriod(1, Season.Spring);
        Assert.Equal(160, Single(sim, TaxKind.IngresosBrutos, spring).Principal); // 10.000 × 1% + 2.000 × 3%
        Assert.Equal(100, Single(sim, TaxKind.SeguridadHigiene, spring).Principal); // 72 < mínimo 100
        var summer = new FiscalPeriod(1, Season.Summer);
        Assert.Equal(150, Single(sim, TaxKind.Monotributo, summer).Principal);
        Assert.Equal(150, Single(sim, TaxKind.Abl, summer).Principal);
        Assert.DoesNotContain(sim.Taxes.State.Obligations, o => o.Kind == TaxKind.Iva);
        Assert.Equal(new EconomicDate(1, Season.Summer, 15), Single(sim, TaxKind.IngresosBrutos, spring).DueDate);
        AssertBalanced(sim);
    }

    [Fact]
    public void Iva_DebitMinusCreditWithCarryForward()
    {
        EconomySimulation sim = NewGame();
        sim.Taxes.SetRegime(TaxRegime.ResponsableInscripto, Spring1);

        sim.RecordShopSales(new[] { new SoldItem("24", -75, 1, 1105) }); // agro 10,5%: IVA 105
        sim.RecordPurchase(121);                                          // 21%: IVA 21
        Assert.Equal(105m, Balance(sim, SystemAccounts.IvaDebitoFiscal));
        Assert.Equal(21m, Balance(sim, SystemAccounts.IvaCreditoFiscal));
        Assert.Equal(1000m, sim.Taxes.State.Current.NetSalesByActivity["agro"]);

        TestData.AdvanceTo(sim, new EconomicDate(1, Season.Summer, 1));
        Assert.Equal(84, Single(sim, TaxKind.Iva, new FiscalPeriod(1, Season.Spring)).Principal);
        Assert.Equal(0m, Balance(sim, SystemAccounts.IvaDebitoFiscal));
        Assert.Equal(0m, Balance(sim, SystemAccounts.IvaCreditoFiscal));

        // Verano: solo compras → saldo a favor de 210 que se traslada.
        sim.RecordPurchase(1210);
        TestData.AdvanceTo(sim, new EconomicDate(1, Season.Fall, 1));
        Assert.DoesNotContain(sim.Taxes.State.Obligations, o => o.Kind == TaxKind.Iva && o.Period.Season == Season.Summer);
        Assert.Equal(210m, Balance(sim, SystemAccounts.IvaCreditoFiscal));

        // Otoño: débito de 21 (venta no agro al 21%) se compensa con el saldo a favor.
        sim.RecordShopSales(new[] { new SoldItem("999", -24, 1, 121) });
        TestData.AdvanceTo(sim, new EconomicDate(1, Season.Winter, 1));
        Assert.DoesNotContain(sim.Taxes.State.Obligations, o => o.Kind == TaxKind.Iva && o.Period.Season == Season.Fall);
        Assert.Equal(189m, Balance(sim, SystemAccounts.IvaCreditoFiscal));
        AssertBalanced(sim);
    }

    [Fact]
    public void Monotributo_RecategorizesAfterSummerAndExcludesAboveTheCap()
    {
        EconomySimulation sim = NewGame();
        sim.RecordShopSales(new[] { new SoldItem("24", -75, 1, 20_000) });
        TestData.AdvanceTo(sim, new EconomicDate(1, Season.Summer, 1));
        Assert.Equal("A", sim.Taxes.Category); // después de primavera no se recategoriza

        sim.RecordShopSales(new[] { new SoldItem("24", -75, 1, 20_000) });
        DayStartedInfo fall = sim.OnDayStarted(new EconomicDate(1, Season.Fall, 1));
        TestData.AdvanceTo(sim, new EconomicDate(1, Season.Fall, 1));

        // 40.000 en 2 estaciones → 80.000 anualizado → categoría D.
        Assert.Equal("D", sim.Taxes.Category);
        Assert.Contains(fall.TaxNotices, n => n.Kind == TaxNoticeKind.Recategorized && n.Detail == "D");
        Assert.Equal(850, Single(sim, TaxKind.Monotributo, new FiscalPeriod(1, Season.Fall)).Principal);

        sim.RecordShopSales(new[] { new SoldItem("24", -75, 1, 2_100_000) });
        DayStartedInfo winter = sim.OnDayStarted(new EconomicDate(1, Season.Winter, 1));
        TestData.AdvanceTo(sim, new EconomicDate(1, Season.Winter, 1));
        Assert.Equal(TaxRegime.ResponsableInscripto, sim.Taxes.Regime);
        Assert.Contains(winter.TaxNotices, n => n.Kind == TaxNoticeKind.Excluded);
        Assert.DoesNotContain(sim.Taxes.State.Obligations, o => o.Kind == TaxKind.Monotributo && o.Period.Season == Season.Winter);
    }

    [Fact]
    public void Ganancias_AnnualReturnWithScaleMinimumAndAdvances()
    {
        EconomySimulation sim = NewGame(cash: 0);
        sim.Taxes.SetRegime(TaxRegime.ResponsableInscripto, Spring1);
        sim.RecordShopSales(new[] { new SoldItem("24", -75, 1, 331_500) }); // neto 300.000
        sim.RecordPurchase(60_500);                                         // neto 50.000

        TestData.AdvanceTo(sim, new EconomicDate(2, Season.Spring, 1));

        // Deducibles del año 1: IIBB 3.000 + SyH (1.800 + 3 × 100) + ABL 4 × 150 = 5.700.
        // Imponible: 300.000 − 50.000 − 5.700 − 15.000 = 229.300 → escala = 30.267.
        TaxObligation ganancias = Single(sim, TaxKind.Ganancias, new FiscalPeriod(1, Season.Winter));
        Assert.Equal(30_267, ganancias.Principal);
        Assert.Equal(new EconomicDate(2, Season.Spring, 22), ganancias.DueDate); // el 21 es domingo
        Assert.Equal(30_267m, Balance(sim, SystemAccounts.Ganancias));

        TestData.AdvanceTo(sim, new EconomicDate(3, Season.Spring, 1));
        var advances = sim.Taxes.State.Obligations.Where(o => o.Kind == TaxKind.GananciasAnticipo).ToList();
        Assert.Equal(new[] { Season.Summer, Season.Fall, Season.Winter }, advances.Select(a => a.Period.Season));
        Assert.All(advances, a => Assert.Equal(7_567, a.Issued)); // el jugador no paga nada y quiebra: se compara lo emitido
        Assert.Equal(2, advances[0].Period.Year);

        // En el año 2 no hubo ventas: el impuesto es 0 y los anticipos quedan como saldo a favor.
        Assert.DoesNotContain(sim.Taxes.State.Obligations, o => o.Kind == TaxKind.Ganancias && o.Period.Year == 2);
        Assert.Equal(3 * 7_567m, Balance(sim, SystemAccounts.AnticiposGanancias));
        AssertBalanced(sim);
    }

    [Fact]
    public void Mora_InterestIntimationFineAndPayment()
    {
        EconomySimulation sim = NewGame(cash: 10_000);
        TaxObligation fee = Single(sim, TaxKind.Monotributo, Spring1.FiscalPeriod);

        DayStartedInfo dueDay = sim.OnDayStarted(new EconomicDate(1, Season.Spring, 15));
        Assert.Equal(ObligationStatus.Pending, fee.Status);
        Assert.Contains(fee, sim.Taxes.DueOn(sim.CurrentDate));

        DayStartedInfo overdue = sim.OnDayStarted(new EconomicDate(1, Season.Spring, 16));
        Assert.Equal(ObligationStatus.Overdue, fee.Status);
        Assert.Contains(overdue.TaxNotices, n => n.Kind == TaxNoticeKind.Overdue && n.ObligationId == fee.Id);

        TestData.AdvanceTo(sim, new EconomicDate(1, Season.Spring, 22));
        Assert.Equal(ObligationStatus.Intimated, fee.Status);
        Assert.Equal(new EconomicDate(1, Season.Summer, 1), fee.IntimationDeadline);

        TestData.AdvanceTo(sim, new EconomicDate(1, Season.Summer, 2));
        Assert.Equal(ObligationStatus.InCollection, fee.Status);
        Assert.Equal(15, fee.Fine);
        Assert.Equal(150m * 0.001m * 15, fee.Interest); // 15 días desde el vencimiento

        long total = fee.Total;
        Assert.Equal(150 + 2 + 15, total);
        Assert.Equal(total, sim.PayObligation(fee.Id));
        Assert.True(fee.IsPaid);
        Assert.Null(sim.PayObligation(fee.Id));
        Assert.Equal(10_000m - total, Balance(sim, SystemAccounts.Caja));
        // 2 de intereses + 15 de multa de esta boleta + 15 de multa del ABL, que también quedó impago.
        Assert.Equal(32m, Balance(sim, SystemAccounts.InteresesYMultasFiscales));
        AssertBalanced(sim);
    }

    [Fact]
    public void AutoDebit_PaysOnTheDueDateEvenWhenDaysAreSkipped()
    {
        EconomySimulation sim = NewGame();
        DayStartedInfo info = sim.OnDayStarted(new EconomicDate(1, Season.Summer, 2), autoDebitBudget: 10_000);

        // Las boletas de primavera se pagaron el lunes 15 (su vencimiento), sin intereses ni multa.
        TaxObligation fee = Single(sim, TaxKind.Monotributo, Spring1.FiscalPeriod);
        Assert.True(fee.IsPaid);
        Assert.Equal(new EconomicDate(1, Season.Spring, 15), fee.PaidOn);
        Assert.Equal(150, fee.PaidAmount);
        Assert.Contains(info.AutoDebited, p => p.ObligationId == fee.Id && p.Amount == 150);
        Assert.DoesNotContain(info.TaxNotices, n => n.Kind is TaxNoticeKind.Overdue or TaxNoticeKind.Intimated or TaxNoticeKind.InCollection);
        Assert.Equal(0m, Balance(sim, SystemAccounts.InteresesYMultasFiscales));
        Assert.Equal(10_000m - info.AutoDebitTotal, Balance(sim, SystemAccounts.Caja));
    }

    [Fact]
    public void AutoDebit_WithoutEnoughGold_LeavesTheBillOverdue()
    {
        EconomySimulation sim = NewGame(cash: 100);
        DayStartedInfo info = sim.OnDayStarted(new EconomicDate(1, Season.Spring, 20), autoDebitBudget: 100);
        Assert.Empty(info.AutoDebited);
        Assert.Equal(ObligationStatus.Overdue, Single(sim, TaxKind.Monotributo, Spring1.FiscalPeriod).Status);
    }

    [Fact]
    public void LateInterest_ScalesWithDifficulty()
    {
        EconomySimulation argentina = NewGame("argentina");
        TestData.AdvanceTo(argentina, new EconomicDate(1, Season.Spring, 20));
        TaxObligation fee = Single(argentina, TaxKind.Monotributo, Spring1.FiscalPeriod);
        Assert.Equal(180m * 0.001m * 1.5m * 5, fee.Interest);
    }

    [Fact]
    public void AYearOfPlay_KeepsTheBooksBalancedAndCashEqualToGold()
    {
        foreach (bool ri in new[] { false, true })
        {
            EconomySimulation sim = NewGame(cash: 500, farm: FarmWithCoop);
            if (ri)
            {
                sim.Taxes.SetRegime(TaxRegime.ResponsableInscripto, Spring1);
            }

            long gold = 500;
            var rng = new ZuzuMarket.Core.Random.DeterministicRandom(ri ? 2UL : 1UL);
            for (int day = 0; day < EconomicDate.DaysPerYear + 30; day++)
            {
                // Durante el día: compras y ventas en tiendas.
                long purchase = rng.NextInt(0, 200);
                if (purchase > 0 && gold >= purchase)
                {
                    sim.RecordPurchase(purchase);
                    gold -= purchase;
                }

                long shopSale = rng.NextInt(0, 300);
                sim.RecordShopSales(new[] { new SoldItem("378", -15, 10, shopSale) });
                gold += shopSale;

                // A la noche: envío, el oro llega, se concilia y se guarda.
                long shipped = rng.NextInt(0, 1500);
                sim.RecordShipmentSales(new[] { new SoldItem("24", -75, 20, shipped) });
                gold += shipped;
                sim.ReconcileCash(gold, "Conciliación");

                // A la mañana: nuevo día, débito automático.
                sim.OnDayStarted(sim.CurrentDate.AddDays(1), Weather.Sun, FarmWithCoop);
                foreach (TaxObligation due in sim.Taxes.DueOn(sim.CurrentDate))
                {
                    if (gold >= due.Total)
                    {
                        gold -= sim.PayObligation(due.Id)!.Value;
                    }
                }
            }

            Assert.Equal(gold, Balance(sim, SystemAccounts.Caja));
            Assert.Equal(0m, Balance(sim, SystemAccounts.IngresosNoClasificados));
            Assert.Equal(0m, Balance(sim, SystemAccounts.EgresosNoClasificados));
            Assert.Equal(0m, Balance(sim, SystemAccounts.DeudoresPorVentas));
            AssertBalanced(sim);
            Assert.Contains(sim.Taxes.State.Obligations, o => o.Kind == TaxKind.IngresosBrutos);
            Assert.Equal(ri, sim.Taxes.State.Obligations.Any(o => o.Kind == TaxKind.Iva));
            Assert.Equal(ri, sim.Taxes.State.Obligations.Any(o => o.Kind == TaxKind.Ganancias));
            Assert.All(sim.Taxes.State.Obligations.Where(o => o.DueDate < sim.CurrentDate), o => Assert.True(o.IsPaid));
        }
    }

    [Fact]
    public void Obligations_SurviveSaveAndLoad()
    {
        EconomySimulation sim = NewGame(farm: FarmWithCoop);
        TestData.AdvanceTo(sim, new EconomicDate(1, Season.Summer, 3), farm: FarmWithCoop);

        EconomySimulation loaded = EconomySimulation.Load(TestData.WithoutInflation(), WorldStateSerializer.Deserialize(WorldStateSerializer.Serialize(sim.Snapshot())));
        Assert.Equal(
            sim.Taxes.State.Obligations.Select(o => (o.Id, o.Kind, o.Principal, o.Status, o.Interest, o.DueDate)),
            loaded.Taxes.State.Obligations.Select(o => (o.Id, o.Kind, o.Principal, o.Status, o.Interest, o.DueDate)));
        Assert.Equal(sim.Taxes.Category, loaded.Taxes.Category);
        Assert.True(loaded.Taxes.Started);

        JournalEntry withKey = loaded.Ledger.Entries.First(e => e.DescriptionKey == "entry.tax-issued");
        Assert.Equal("Monotributo", withKey.DescriptionArgs!["tax"]);
    }

    [Fact]
    public void ProgressiveScale_IsPiecewise()
    {
        GananciasRules rules = TestData.Real.Taxes.Ganancias;
        Assert.Equal(0m, rules.ProgressiveTax(0, 1));
        Assert.Equal(500m, rules.ProgressiveTax(10_000, 1));
        Assert.Equal(1_000m + 2_700m, rules.ProgressiveTax(50_000, 1));
        Assert.Equal(2_000m, rules.ProgressiveTax(40_000, 2)); // con IPC 2 el primer tramo llega a 40.000
    }
}
