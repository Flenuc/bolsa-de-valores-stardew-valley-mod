using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Ledger;

/// <summary>Renglón de un asiento: un importe al Debe o al Haber de una cuenta.</summary>
public sealed record JournalLine(string AccountCode, decimal Debit, decimal Credit)
{
    public static JournalLine ToDebit(string accountCode, decimal amount) => new(accountCode, amount, 0m);

    public static JournalLine ToCredit(string accountCode, decimal amount) => new(accountCode, 0m, amount);
}

/// <summary>Asiento contable ya registrado (inmutable).</summary>
public sealed record JournalEntry(
    long Id,
    string EntityId,
    EconomicDate Date,
    string Description,
    string Source,
    IReadOnlyList<JournalLine> Lines)
{
    public decimal TotalDebit => Lines.Sum(l => l.Debit);

    public decimal TotalCredit => Lines.Sum(l => l.Credit);
}

/// <summary>Orígenes de asientos conocidos (para filtrar y auditar).</summary>
public static class EntrySources
{
    public const string Apertura = "apertura";
    public const string Conciliacion = "conciliacion";
    public const string Ajuste = "ajuste";
}

public sealed class LedgerException : Exception
{
    public LedgerException(string message)
        : base(message)
    {
    }
}
