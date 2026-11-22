using ZuzuMarket.Core.Ledger;
using ZuzuMarket.Core.Money;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Tests.Ledger;

public class GeneralLedgerTests
{
    private static readonly EconomicDate Today = new(1, Season.Spring, 3);

    private static GeneralLedger NewLedger()
    {
        var ledger = new GeneralLedger(TestData.Real.Chart);
        ledger.AddEntity(LedgerEntities.Jugador);
        ledger.AddEntity(LedgerEntities.Granja);
        return ledger;
    }

    [Fact]
    public void Post_BalancedEntry_UpdatesBalances()
    {
        GeneralLedger ledger = NewLedger();
        ledger.Post(LedgerEntities.Jugador, Today, "Apertura", EntrySources.Apertura,
            JournalLine.ToDebit(SystemAccounts.Caja, 500),
            JournalLine.ToCredit(SystemAccounts.Capital, 500));
        ledger.Post(LedgerEntities.Jugador, Today, "Compra de semillas", EntrySources.Ajuste,
            JournalLine.ToDebit("5.2.01", 120.50m),
            JournalLine.ToCredit(SystemAccounts.Caja, 120.50m));

        Assert.Equal(379.50m, ledger.GetBalance(LedgerEntities.Jugador, SystemAccounts.Caja));
        Assert.Equal(500m, ledger.GetBalance(LedgerEntities.Jugador, SystemAccounts.Capital));
        Assert.Equal(120.50m, ledger.GetBalance(LedgerEntities.Jugador, "5.2.01"));
        Assert.Equal(0m, ledger.GetBalance(LedgerEntities.Granja, SystemAccounts.Caja));
        Assert.Equal(2, ledger.Entries.Count);
        Assert.Equal(new long[] { 1, 2 }, ledger.Entries.Select(e => e.Id));
    }

    [Fact]
    public void Post_MultiLineEntry()
    {
        GeneralLedger ledger = NewLedger();
        ledger.Post(LedgerEntities.Granja, Today, "Venta con impuesto", EntrySources.Ajuste,
            JournalLine.ToDebit(SystemAccounts.Caja, 121),
            JournalLine.ToCredit("4.1.01", 100),
            JournalLine.ToCredit("2.1.02", 21));

        Assert.Equal(121m, ledger.GetBalance(LedgerEntities.Granja, SystemAccounts.Caja));
        Assert.Equal(21m, ledger.GetBalance(LedgerEntities.Granja, "2.1.02"));
    }

    [Fact]
    public void Post_UnbalancedEntry_IsRejectedAndNothingIsRecorded()
    {
        GeneralLedger ledger = NewLedger();
        Assert.Throws<LedgerException>(() => ledger.Post(LedgerEntities.Jugador, Today, "Mal", EntrySources.Ajuste,
            JournalLine.ToDebit(SystemAccounts.Caja, 100),
            JournalLine.ToCredit(SystemAccounts.Capital, 99.99m)));

        Assert.Empty(ledger.Entries);
        Assert.Equal(0m, ledger.GetBalance(LedgerEntities.Jugador, SystemAccounts.Caja));
    }

    [Fact]
    public void Post_InvalidInput_IsRejected()
    {
        GeneralLedger ledger = NewLedger();
        JournalLine debit = JournalLine.ToDebit(SystemAccounts.Caja, 10);
        JournalLine credit = JournalLine.ToCredit(SystemAccounts.Capital, 10);

        Assert.Throws<LedgerException>(() => ledger.Post("desconocida", Today, "x", EntrySources.Ajuste, debit, credit));
        Assert.Throws<LedgerException>(() => ledger.Post(LedgerEntities.Jugador, Today, "x", EntrySources.Ajuste, debit, JournalLine.ToCredit("9.9.99", 10)));
        Assert.Throws<LedgerException>(() => ledger.Post(LedgerEntities.Jugador, Today, "x", EntrySources.Ajuste, debit));
        Assert.Throws<LedgerException>(() => ledger.Post(LedgerEntities.Jugador, Today, " ", EntrySources.Ajuste, debit, credit));
        Assert.Throws<LedgerException>(() => ledger.Post(LedgerEntities.Jugador, Today, "x", EntrySources.Ajuste,
            new JournalLine(SystemAccounts.Caja, 10, 10), credit));
        Assert.Throws<LedgerException>(() => ledger.Post(LedgerEntities.Jugador, Today, "x", EntrySources.Ajuste,
            JournalLine.ToDebit(SystemAccounts.Caja, -10), JournalLine.ToCredit(SystemAccounts.Capital, -10)));
        Assert.Throws<LedgerException>(() => ledger.Post(LedgerEntities.Jugador, Today, "x", EntrySources.Ajuste,
            JournalLine.ToDebit(SystemAccounts.Caja, 0), JournalLine.ToCredit(SystemAccounts.Capital, 0)));
        Assert.Empty(ledger.Entries);
    }

    [Fact]
    public void Post_RoundsToTwoDecimals()
    {
        GeneralLedger ledger = NewLedger();
        JournalEntry entry = ledger.Post(LedgerEntities.Jugador, Today, "Redondeo", EntrySources.Ajuste,
            JournalLine.ToDebit(SystemAccounts.Caja, 10.005m),
            JournalLine.ToCredit(SystemAccounts.Capital, 10.004m));

        Assert.Equal(10.00m, entry.Lines[0].Debit);
        Assert.Equal(10.00m, entry.Lines[1].Credit);
    }

    [Fact]
    public void TrialBalance_SumsMatch()
    {
        GeneralLedger ledger = NewLedger();
        ledger.Post(LedgerEntities.Jugador, Today, "Apertura", EntrySources.Apertura,
            JournalLine.ToDebit(SystemAccounts.Caja, 1000), JournalLine.ToCredit(SystemAccounts.Capital, 1000));
        ledger.Post(LedgerEntities.Jugador, Today, "Ingreso", EntrySources.Conciliacion,
            JournalLine.ToDebit(SystemAccounts.Caja, 250), JournalLine.ToCredit(SystemAccounts.IngresosNoClasificados, 250));
        ledger.Post(LedgerEntities.Jugador, Today, "Egreso", EntrySources.Conciliacion,
            JournalLine.ToDebit(SystemAccounts.EgresosNoClasificados, 75), JournalLine.ToCredit(SystemAccounts.Caja, 75));

        IReadOnlyList<TrialBalanceRow> rows = ledger.GetTrialBalance(LedgerEntities.Jugador);
        Assert.Equal(rows.Sum(r => r.Debits), rows.Sum(r => r.Credits));
        Assert.Equal(new[] { SystemAccounts.Caja, SystemAccounts.Capital, SystemAccounts.IngresosNoClasificados, SystemAccounts.EgresosNoClasificados },
            rows.Select(r => r.Account.Code));
        Assert.Equal(1175m, rows.Single(r => r.Account.Code == SystemAccounts.Caja).Balance);
        Assert.Empty(ledger.GetTrialBalance(LedgerEntities.Granja));
    }

    [Fact]
    public void RecentEntries_NewestFirstAndFilteredByEntity()
    {
        GeneralLedger ledger = NewLedger();
        for (int i = 1; i <= 5; i++)
        {
            string entity = i % 2 == 0 ? LedgerEntities.Granja : LedgerEntities.Jugador;
            ledger.Post(entity, Today, $"Asiento {i}", EntrySources.Ajuste,
                JournalLine.ToDebit(SystemAccounts.Caja, i), JournalLine.ToCredit(SystemAccounts.Capital, i));
        }

        Assert.Equal(new long[] { 5, 4, 3 }, ledger.GetRecentEntries(3).Select(e => e.Id));
        Assert.Equal(new long[] { 4, 2 }, ledger.GetRecentEntries(10, LedgerEntities.Granja).Select(e => e.Id));
    }

    [Fact]
    public void Rebuild_FromState_RestoresBalances()
    {
        GeneralLedger original = NewLedger();
        original.Post(LedgerEntities.Jugador, Today, "Apertura", EntrySources.Apertura,
            JournalLine.ToDebit(SystemAccounts.Caja, 300), JournalLine.ToCredit(SystemAccounts.Capital, 300));

        var rebuilt = new GeneralLedger(TestData.Real.Chart, original.State);
        Assert.Equal(300m, rebuilt.GetBalance(LedgerEntities.Jugador, SystemAccounts.Caja));
        JournalEntry next = rebuilt.Post(LedgerEntities.Jugador, Today, "Otro", EntrySources.Ajuste,
            JournalLine.ToDebit(SystemAccounts.Caja, 1), JournalLine.ToCredit(SystemAccounts.Capital, 1));
        Assert.Equal(2, next.Id);
    }

    [Fact]
    public void Rebuild_WithUnknownAccount_Throws()
    {
        var state = new LedgerState { Entities = { LedgerEntities.Jugador } };
        state.Entries.Add(new JournalEntry(1, LedgerEntities.Jugador, Today, "x", EntrySources.Ajuste, new[]
        {
            JournalLine.ToDebit("9.9.99", 1), JournalLine.ToCredit(SystemAccounts.Capital, 1),
        }));

        Assert.Throws<LedgerException>(() => new GeneralLedger(TestData.Real.Chart, state));
    }

    [Theory]
    [InlineData("2.5", 2, "0.5")]
    [InlineData("3.5", 4, "-0.5")]
    [InlineData("10.49", 10, "0.49")]
    [InlineData("-1.5", -2, "0.5")]
    public void MoneyRounding_IsHalfEvenWithResidue(string amount, long expectedGold, string expectedResidue)
    {
        long gold = MoneyRounding.ToGold(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), out decimal residue);
        Assert.Equal(expectedGold, gold);
        Assert.Equal(decimal.Parse(expectedResidue, System.Globalization.CultureInfo.InvariantCulture), residue);
    }

    [Fact]
    public void MoneyRounding_TwoDecimals()
    {
        Assert.Equal(1.24m, MoneyRounding.Round(1.245m));
        Assert.Equal(1.26m, MoneyRounding.Round(1.255m));
        Assert.True(MoneyRounding.HasAtMostTwoDecimals(1.25m));
        Assert.False(MoneyRounding.HasAtMostTwoDecimals(1.251m));
    }
}
