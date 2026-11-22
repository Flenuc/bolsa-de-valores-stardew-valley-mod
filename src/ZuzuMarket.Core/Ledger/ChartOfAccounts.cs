namespace ZuzuMarket.Core.Ledger;

/// <summary>Plan de cuentas (se carga desde <c>data/accounts.json</c>).</summary>
public sealed class ChartOfAccounts
{
    private readonly Dictionary<string, Account> _accounts;

    public ChartOfAccounts(IEnumerable<Account> accounts)
    {
        _accounts = new Dictionary<string, Account>(StringComparer.Ordinal);
        foreach (Account account in accounts)
        {
            if (!_accounts.TryAdd(account.Code, account))
            {
                throw new ArgumentException($"Código de cuenta duplicado: {account.Code}.", nameof(accounts));
            }
        }
    }

    public IEnumerable<Account> Accounts => _accounts.Values.OrderBy(a => a.Code, StringComparer.Ordinal);

    public int Count => _accounts.Count;

    public bool Contains(string code) => _accounts.ContainsKey(code);

    public Account Get(string code) =>
        _accounts.TryGetValue(code, out Account? account)
            ? account
            : throw new KeyNotFoundException($"La cuenta {code} no existe en el plan de cuentas.");

    public bool TryGet(string code, out Account? account) => _accounts.TryGetValue(code, out account);
}
