using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Ledger;

/// <summary>Renglón de un asiento: un importe al Debe o al Haber de una cuenta.</summary>
public sealed record JournalLine(string AccountCode, decimal Debit, decimal Credit)
{
    public static JournalLine ToDebit(string accountCode, decimal amount) => new(accountCode, amount, 0m);

    public static JournalLine ToCredit(string accountCode, decimal amount) => new(accountCode, 0m, amount);
}

/// <summary>Asiento contable ya registrado (inmutable).</summary>
/// <param name="Description">Texto de respaldo (en el idioma en que se registró).</param>
/// <param name="DescriptionKey">Clave de traducción para mostrar el asiento en el idioma actual (opcional).</param>
/// <param name="DescriptionArgs">Parámetros de la traducción.</param>
public sealed record JournalEntry(
    long Id,
    string EntityId,
    EconomicDate Date,
    string Description,
    string Source,
    IReadOnlyList<JournalLine> Lines,
    string? DescriptionKey = null,
    IReadOnlyDictionary<string, string>? DescriptionArgs = null)
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
    public const string Venta = "venta";
    public const string Compra = "compra";
    public const string Impuesto = "impuesto";
    public const string Pago = "pago";
    public const string Cobro = "cobro";
}

/// <summary>Descripción de un asiento: texto de respaldo + clave de traducción opcional.</summary>
public sealed record EntryText(string Fallback, string? Key = null, IReadOnlyDictionary<string, string>? Args = null)
{
    public static implicit operator EntryText(string text) => new(text);
}

public sealed class LedgerException : Exception
{
    public LedgerException(string message)
        : base(message)
    {
    }
}
