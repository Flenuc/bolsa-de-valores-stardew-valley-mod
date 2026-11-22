using ZuzuMarket.Core.Ledger;
using ZuzuMarket.Core.Money;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Markets;

/// <summary>Caución tomadora (financiamiento con los títulos en garantía), ventas forzosas y liquidación total.</summary>
public sealed partial class Broker
{
    /// <summary>Valor de mercado de acciones, bonos y Letras (lo que se puede dejar en garantía).</summary>
    public decimal SecuritiesValue(EconomicDate date, int timeOfDay)
    {
        double stocks = _s.Broker.Stocks.Sum(kv => _stocks.Find(kv.Key) is null ? 0 : _stocks.PriceAt(kv.Key, timeOfDay) * kv.Value.Quantity);
        double letras = _s.Broker.Letras.Sum(kv => FindLetra(kv.Key) is { } l ? LetraPrice(l, date) * kv.Value.Quantity : 0);
        double bonds = _s.Broker.Bonds.Sum(kv => FindBond(kv.Key) is { } b ? BondPrice(b, date) * kv.Value.Quantity : 0);
        return MoneyRounding.Round((decimal)(stocks + letras + bonds));
    }

    public double RepoRate => CaucionRate + _data.Legal.RepoSpread;

    public decimal RepoLimit(EconomicDate date, int timeOfDay) =>
        Math.Max(0, Math.Floor((SecuritiesValue(date, timeOfDay) * _data.Legal.RepoMaxRatio) - _s.Broker.Repos.Sum(r => r.Principal)));

    public (EconomicDate Maturity, decimal Interest) RepoTerms(decimal amount, int days, EconomicDate date)
    {
        EconomicDate maturity = date.AddDays(Math.Max(1, days)).NextBusinessDayOrSelf();
        return (maturity, MoneyRounding.Round(amount * (decimal)(RepoRate * date.DaysUntil(maturity) / EconomicDate.DaysPerSeason)));
    }

    public TradeError ValidateRepo(decimal amount, int days, EconomicDate date, int timeOfDay)
    {
        if (!IsOpen)
        {
            return TradeError.NoAccount;
        }

        if (!IsMarketOpen(date, timeOfDay))
        {
            return TradeError.MarketClosed;
        }

        if (IsInhibited())
        {
            return TradeError.Inhibited;
        }

        if (!_data.Legal.RepoTerms.Contains(days))
        {
            return TradeError.InvalidTerm;
        }

        if (amount < Rules.CaucionMinimum)
        {
            return TradeError.BelowMinimum;
        }

        return amount > RepoLimit(date, timeOfDay) ? TradeError.AboveLimit : TradeError.None;
    }

    /// <summary>Toma oro en caución: entra a la cuenta comitente y se devuelve con interés al vencer.</summary>
    public TradeError BorrowRepo(decimal amount, int days, EconomicDate date, int timeOfDay)
    {
        TradeError error = ValidateRepo(amount, days, date, timeOfDay);
        if (error != TradeError.None)
        {
            return error;
        }

        (EconomicDate maturity, decimal interest) = RepoTerms(amount, days, date);
        _s.Broker.Repos.Add(new Caucion { Id = _s.Broker.NextId++, Principal = amount, Rate = RepoRate, Start = date, Maturity = maturity, Interest = interest });
        Post(date, $"Caución tomadora a {days} días", "entry.repo-borrowed", "",
            JournalLine.ToDebit(SystemAccounts.CuentaComitente, amount),
            JournalLine.ToCredit(SystemAccounts.CaucionTomadora, amount));
        return TradeError.None;
    }

    /// <summary>Al vencer, la caución tomadora se paga desde la cuenta comitente; si no alcanza, se venden títulos.</summary>
    public List<MarketNotice> SettleRepos(EconomicDate date)
    {
        var notices = new List<MarketNotice>();
        foreach (Caucion repo in _s.Broker.Repos.Where(r => r.Maturity <= date).ToList())
        {
            decimal total = repo.Principal + repo.Interest;
            if (Cash < total)
            {
                decimal sold = SellUntil(total, date);
                notices.Add(new MarketNotice(MarketNoticeKind.RepoForcedSale, date, repo.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), sold));
            }

            RepayRepo(repo, date);
            notices.Add(new MarketNotice(MarketNoticeKind.RepoRepaid, date, repo.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), total));
        }

        return notices;
    }

    /// <summary>Quiebra: cierra derivados, vende todos los títulos, cancela cauciones y devuelve las garantías.</summary>
    public void LiquidateEverything(EconomicDate date)
    {
        _forcing = true;
        try
        {
            Liquidate(date, MarketHours.Open);
            foreach (OptionPosition p in _s.Derivatives.OptionPositions.Where(p => p.Quantity > 0).ToList())
            {
                DerivativeQuote q = QuoteOption(p.SeriesId, TradeSide.Sell, p.Quantity, date, MarketHours.Open) with { Error = TradeError.None };
                ApplyOptionTrade(q, date, MarketHours.Open);
            }

            SellUntil(decimal.MaxValue, date);

            foreach (Caucion c in _s.Broker.Cauciones.ToList())
            {
                Post(date, "Cancelación anticipada de caución", "entry.caucion-collected", "",
                    JournalLine.ToDebit(SystemAccounts.CuentaComitente, c.Principal),
                    JournalLine.ToCredit(SystemAccounts.CaucionesColocadas, c.Principal));
                _s.Broker.Cauciones.Remove(c);
            }

            foreach (Caucion repo in _s.Broker.Repos.ToList())
            {
                RepayRepo(repo, date);
            }

            ReleaseAllMargin(date);
        }
        finally
        {
            _forcing = false;
        }
    }

    /// <summary>Vende títulos (acciones, después bonos y Letras) hasta que el saldo llegue a <paramref name="target"/>.</summary>
    private decimal SellUntil(decimal target, EconomicDate date)
    {
        bool wasForcing = _forcing;
        _forcing = true;
        decimal before = Cash;
        try
        {
            foreach ((string id, long quantity) in _s.Broker.Stocks.Select(kv => (kv.Key, kv.Value.Quantity)).ToList()
                .Concat(_s.Broker.Bonds.Select(kv => (kv.Key, kv.Value.Quantity)).ToList())
                .Concat(_s.Broker.Letras.Select(kv => (kv.Key, kv.Value.Quantity)).ToList()))
            {
                if (Cash >= target)
                {
                    break;
                }

                Execute(id, TradeSide.Sell, quantity, date, MarketHours.Open);
            }
        }
        finally
        {
            _forcing = wasForcing;
        }

        return Cash - before;
    }

    private void RepayRepo(Caucion repo, EconomicDate date)
    {
        var lines = new List<JournalLine> { JournalLine.ToDebit(SystemAccounts.CaucionTomadora, repo.Principal) };
        if (repo.Interest > 0)
        {
            lines.Add(JournalLine.ToDebit(SystemAccounts.InteresesBancarios, repo.Interest));
        }

        lines.Add(JournalLine.ToCredit(SystemAccounts.CuentaComitente, repo.Principal + repo.Interest));
        Post(date, "Pago de caución tomadora", "entry.repo-repaid", "", lines.ToArray());
        _s.Broker.Repos.Remove(repo);
    }
}
