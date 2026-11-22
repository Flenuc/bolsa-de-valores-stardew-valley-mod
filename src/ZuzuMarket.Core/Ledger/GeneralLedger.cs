using ZuzuMarket.Core.Money;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Ledger;

/// <summary>Estado serializable del libro contable.</summary>
public sealed class LedgerState
{
    public List<string> Entities { get; set; } = new();

    public List<JournalEntry> Entries { get; set; } = new();

    public long NextEntryId { get; set; } = 1;
}

/// <summary>Fila del balance de sumas y saldos.</summary>
public sealed record TrialBalanceRow(Account Account, decimal Debits, decimal Credits)
{
    /// <summary>Saldo en el sentido natural de la cuenta (deudor para Activo/Egreso, acreedor para el resto).</summary>
    public decimal Balance => Account.Type.IsDebitNormal() ? Debits - Credits : Credits - Debits;
}

/// <summary>
/// Libro diario y mayor por partida doble. Todo movimiento de dinero del mod pasa por acá:
/// cada asiento debe estar balanceado (Debe = Haber).
/// </summary>
public sealed class GeneralLedger
{
    private readonly ChartOfAccounts _chart;
    private readonly LedgerState _state;
    private readonly HashSet<string> _entities;
    private readonly Dictionary<(string Entity, string Account), (decimal Debits, decimal Credits)> _totals = new();

    public GeneralLedger(ChartOfAccounts chart, LedgerState? state = null)
    {
        _chart = chart;
        _state = state ?? new LedgerState();
        _entities = new HashSet<string>(_state.Entities, StringComparer.Ordinal);

        foreach (JournalEntry entry in _state.Entries)
        {
            if (!_entities.Contains(entry.EntityId))
            {
                throw new LedgerException($"El asiento {entry.Id} pertenece a una entidad desconocida: {entry.EntityId}.");
            }

            foreach (JournalLine line in entry.Lines)
            {
                if (!_chart.Contains(line.AccountCode))
                {
                    throw new LedgerException($"El asiento {entry.Id} usa la cuenta {line.AccountCode}, que no está en el plan de cuentas.");
                }

                Accumulate(entry.EntityId, line);
            }
        }
    }

    public ChartOfAccounts Chart => _chart;

    public IReadOnlyCollection<string> Entities => _entities;

    public IReadOnlyList<JournalEntry> Entries => _state.Entries;

    /// <summary>Estado para serializar. Es la misma instancia que usa el libro.</summary>
    public LedgerState State => _state;

    public void AddEntity(string entityId)
    {
        if (string.IsNullOrWhiteSpace(entityId))
        {
            throw new ArgumentException("El id de la entidad no puede estar vacío.", nameof(entityId));
        }

        if (_entities.Add(entityId))
        {
            _state.Entities.Add(entityId);
        }
    }

    public bool HasEntity(string entityId) => _entities.Contains(entityId);

    /// <summary>
    /// Registra un asiento. Los importes se redondean a 2 decimales y luego se valida que esté balanceado.
    /// </summary>
    /// <exception cref="LedgerException">Si el asiento es inválido. En ese caso no se registra nada.</exception>
    public JournalEntry Post(string entityId, EconomicDate date, string description, string source, params JournalLine[] lines)
    {
        if (!_entities.Contains(entityId))
        {
            throw new LedgerException($"Entidad contable desconocida: {entityId}.");
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new LedgerException("El asiento necesita una descripción.");
        }

        if (lines is null || lines.Length < 2)
        {
            throw new LedgerException("Un asiento necesita al menos dos renglones.");
        }

        var rounded = new List<JournalLine>(lines.Length);
        foreach (JournalLine line in lines)
        {
            if (!_chart.Contains(line.AccountCode))
            {
                throw new LedgerException($"La cuenta {line.AccountCode} no existe en el plan de cuentas.");
            }

            decimal debit = MoneyRounding.Round(line.Debit);
            decimal credit = MoneyRounding.Round(line.Credit);
            if (debit < 0 || credit < 0)
            {
                throw new LedgerException($"Importe negativo en la cuenta {line.AccountCode}.");
            }

            if ((debit == 0) == (credit == 0))
            {
                throw new LedgerException($"Cada renglón debe tener importe al Debe o al Haber (no ambos ni ninguno): cuenta {line.AccountCode}.");
            }

            rounded.Add(new JournalLine(line.AccountCode, debit, credit));
        }

        decimal totalDebit = rounded.Sum(l => l.Debit);
        decimal totalCredit = rounded.Sum(l => l.Credit);
        if (totalDebit != totalCredit)
        {
            throw new LedgerException($"Asiento desbalanceado: Debe {totalDebit} ≠ Haber {totalCredit}.");
        }

        var entry = new JournalEntry(_state.NextEntryId++, entityId, date, description, source, rounded);
        _state.Entries.Add(entry);
        foreach (JournalLine line in rounded)
        {
            Accumulate(entityId, line);
        }

        return entry;
    }

    /// <summary>Saldo de una cuenta en su sentido natural.</summary>
    public decimal GetBalance(string entityId, string accountCode)
    {
        Account account = _chart.Get(accountCode);
        _totals.TryGetValue((entityId, accountCode), out var t);
        return account.Type.IsDebitNormal() ? t.Debits - t.Credits : t.Credits - t.Debits;
    }

    /// <summary>Balance de sumas y saldos de una entidad (solo cuentas con movimientos).</summary>
    public IReadOnlyList<TrialBalanceRow> GetTrialBalance(string entityId) =>
        _totals
            .Where(kv => kv.Key.Entity == entityId)
            .Select(kv => new TrialBalanceRow(_chart.Get(kv.Key.Account), kv.Value.Debits, kv.Value.Credits))
            .OrderBy(r => r.Account.Code, StringComparer.Ordinal)
            .ToList();

    public IReadOnlyList<JournalEntry> GetRecentEntries(int count, string? entityId = null) =>
        _state.Entries
            .Where(e => entityId is null || e.EntityId == entityId)
            .Reverse()
            .Take(Math.Max(0, count))
            .ToList();

    private void Accumulate(string entityId, JournalLine line)
    {
        var key = (entityId, line.AccountCode);
        _totals.TryGetValue(key, out var t);
        _totals[key] = (t.Debits + line.Debit, t.Credits + line.Credit);
    }
}
