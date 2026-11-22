using Xunit.Abstractions;
using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Ledger;
using ZuzuMarket.Core.Simulation;
using ZuzuMarket.Core.Taxation;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Tests.Balance;

/// <summary>Simulaciones de un año por dificultad con perfiles fijos de jugador (§14, Fase 8: balance).</summary>
public class BalanceTests
{
    private readonly ITestOutputHelper _out;

    public BalanceTests(ITestOutputHelper output)
    {
        _out = output;
    }

    private static readonly EconomicDate Start = new(1, Season.Spring, 1);

    /// <summary>Resultado de un año de un perfil.</summary>
    private sealed record YearResult(string Difficulty, decimal Revenue, decimal TaxesPaid, double Inflation, bool Bankrupt, decimal FinalCash, string Rating);

    /// <summary>
    /// Granjero que vende <paramref name="weeklySales"/> por semana por el cajón (agro), paga con débito automático y
    /// gasta un 30% de lo que vende en tiendas.
    /// </summary>
    private static YearResult Farmer(string difficulty, long weeklySales, ulong seed = 7, int measuredYear = 1)
    {
        EconomySimulation sim = EconomySimulation.StartNewGame(TestData.Load(), seed, Start, difficulty, 1_000);
        sim.OnDayStarted(Start);
        double ipc0 = sim.Inflation.Index;
        decimal taxes = 0;
        decimal revenue = 0;
        bool bankrupt = false;
        while (sim.CurrentDate < new EconomicDate(measuredYear + 1, Season.Spring, 1))
        {
            EconomicDate today = sim.CurrentDate;
            if (today.Year == measuredYear)
            {
                ipc0 = today == new EconomicDate(measuredYear, Season.Spring, 1) ? sim.Inflation.Index : ipc0;
            }

            if (today.Day % 7 == 5)
            {
                long sale = (long)(weeklySales * sim.Inflation.Index);
                sim.RecordShipmentSales(new[] { new SoldItem("24", -75, 1, sale) });
                revenue += today.Year == measuredYear ? sale : 0;
            }

            long pocket = (long)sim.Ledger.GetBalance(LedgerEntities.Jugador, SystemAccounts.Caja)
                + (long)sim.Ledger.GetBalance(LedgerEntities.Jugador, SystemAccounts.DeudoresPorVentas);
            DayStartedInfo info = sim.OnDayStarted(today.AddDays(1), Weather.Sun, null, pocket, pocket);
            taxes += info.AutoDebitTotal;
            bankrupt |= info.Legal.BankruptcyDeclared;
            long gold = pocket - info.AutoDebitTotal;
            sim.ReconcileCash(gold, new EntryText("Conciliación"));
            if (today.Day % 7 == 6 && gold > 0)
            {
                long spend = Math.Min(gold, (long)(weeklySales * sim.Inflation.Index * 0.3));
                sim.RecordPurchase(spend);
                sim.ReconcileCash(gold - spend, new EntryText("Conciliación"));
            }
        }

        // Carga tributaria devengada en el año (boletas emitidas sin anticipos, que son del año siguiente),
        // incluida la declaración de Ganancias que se emite al empezar el año 2.
        decimal accrued = sim.Taxes.State.Obligations.Where(o => o.Kind != TaxKind.GananciasAnticipo && o.Period.Year == measuredYear).Sum(o => (decimal)o.Issued);
        return new YearResult(difficulty, revenue, accrued, (sim.Inflation.Index / ipc0) - 1, bankrupt,
            sim.Ledger.GetBalance(LedgerEntities.Jugador, SystemAccounts.Caja), sim.Legal.Rating);
    }

    private void Print(string profile, YearResult r) =>
        _out.WriteLine($"{profile,-12} {r.Difficulty,-10} ventas {r.Revenue,12:N0} impuestos {r.TaxesPaid,10:N0} ({(r.Revenue > 0 ? r.TaxesPaid / r.Revenue : 0),6:P1}) inflación {r.Inflation,7:P1} caja {r.FinalCash,12:N0} calif {r.Rating} quiebra {r.Bankrupt}");

    [Theory]
    [InlineData("casual")]
    [InlineData("realista")]
    [InlineData("argentina")]
    public void SmallFarmer_PaysModestTaxes_AndNeverGoesBankrupt(string difficulty)
    {
        YearResult r = Farmer(difficulty, 1_500);
        Print("chico", r);
        Assert.False(r.Bankrupt);
        Assert.InRange(r.TaxesPaid / r.Revenue, 0.02m, 0.18m); // Monotributo: carga baja
    }

    [Theory]
    [InlineData("casual", 0.05, 0.11)]
    [InlineData("realista", 0.14, 0.25)]
    [InlineData("argentina", 0.22, 0.36)]
    public void BigFarmer_TaxPressureGrowsWithDifficulty(string difficulty, double min, double max)
    {
        YearResult r = Farmer(difficulty, 150_000, measuredYear: 2); // ~2,4 millones al año: el año 2 es entero como Responsable Inscripto
        Print("grande", r);
        Assert.False(r.Bankrupt);
        Assert.InRange((double)(r.TaxesPaid / r.Revenue), min, max);
    }

    [Theory]
    [InlineData("casual", 0.0, 0.05)]
    [InlineData("realista", 0.05, 0.25)]
    [InlineData("argentina", 0.20, 1.50)]
    public void Inflation_IsInTheDifficultyRange(string difficulty, double min, double max)
    {
        // Promedio de varias semillas: la inflación anual tiene que caer en el rango de la dificultad.
        double average = Enumerable.Range(1, 4).Average(seed => Farmer(difficulty, 1_500, (ulong)seed).Inflation);
        _out.WriteLine($"{difficulty}: inflación anual promedio {average:P1}");
        Assert.InRange(average, min, max);
    }
}
