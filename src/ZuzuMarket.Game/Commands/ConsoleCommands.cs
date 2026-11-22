using System.Globalization;
using System.Text;
using StardewModdingAPI;
using StardewValley;
using ZuzuMarket.Core.Commodities;
using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Events;
using ZuzuMarket.Core.Ledger;
using ZuzuMarket.Core.Macro;
using ZuzuMarket.Core.Markets;
using ZuzuMarket.Core.Random;
using ZuzuMarket.Core.Simulation;
using ZuzuMarket.Core.Taxation;
using ZuzuMarket.Core.Time;
using ZuzuMarket.Game.Bridges;
using ZuzuMarket.Game.Content;
using Season = ZuzuMarket.Core.Time.Season;

namespace ZuzuMarket.Game.Commands;

/// <summary>Comandos de consola de SMAPI para inspeccionar y depurar la simulación.</summary>
internal sealed class ConsoleCommands
{
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly ModConfig _config;
    private readonly Func<EconomySimulation?> _getSimulation;
    private readonly Action _reset;
    private readonly NewsService _news;
    private readonly Municipalidad _municipalidad;
    private readonly TaxMail _taxMail;
    private readonly PhoneService _phone;
    private readonly Action _openTerminal;
    private readonly Action _openSilo;

    public ConsoleCommands(IModHelper helper, IMonitor monitor, ModConfig config, Func<EconomySimulation?> getSimulation, Action reset, NewsService news, Municipalidad municipalidad, TaxMail taxMail, PhoneService phone, Action openTerminal, Action openSilo)
    {
        _openSilo = openSilo;
        _phone = phone;
        _openTerminal = openTerminal;
        _news = news;
        _municipalidad = municipalidad;
        _taxMail = taxMail;
        _helper = helper;
        _monitor = monitor;
        _config = config;
        _getSimulation = getSimulation;
        _reset = reset;
    }

    private ITranslationHelper I18n => _helper.Translation;

    public void Register()
    {
        _helper.ConsoleCommands.Add("zuzu_status", I18n.Get("cmd.status.help"), WithSimulation(Status));
        _helper.ConsoleCommands.Add("zuzu_ledger", I18n.Get("cmd.ledger.help"), WithSimulation(LedgerCmd));
        _helper.ConsoleCommands.Add("zuzu_balance", I18n.Get("cmd.balance.help"), WithSimulation(Balance));
        _helper.ConsoleCommands.Add("zuzu_rng", I18n.Get("cmd.rng.help"), WithSimulation(Rng));
        _helper.ConsoleCommands.Add("zuzu_reset", I18n.Get("cmd.reset.help"), WithSimulation(Reset));
        _helper.ConsoleCommands.Add("zuzu_prices", I18n.Get("cmd.prices.help"), WithSimulation(Prices));
        _helper.ConsoleCommands.Add("zuzu_inflation", I18n.Get("cmd.inflation.help"), WithSimulation(InflationCmd));
        _helper.ConsoleCommands.Add("zuzu_events", I18n.Get("cmd.events.help"), WithSimulation(EventsCmd));
        _helper.ConsoleCommands.Add("zuzu_event", I18n.Get("cmd.event.help"), WithSimulation(ForceEvent));
        _helper.ConsoleCommands.Add("zuzu_taxes", I18n.Get("cmd.taxes.help"), WithSimulation(TaxesCmd));
        _helper.ConsoleCommands.Add("zuzu_results", I18n.Get("cmd.results.help"), WithSimulation(Results));
        _helper.ConsoleCommands.Add("zuzu_here", I18n.Get("cmd.here.help"), WithSimulation(Here));
        _helper.ConsoleCommands.Add("zuzu_pay", I18n.Get("cmd.pay.help"), WithSimulation(PayCmd));
        _helper.ConsoleCommands.Add("zuzu_regime", I18n.Get("cmd.regime.help"), WithSimulation(RegimeCmd));
        _helper.ConsoleCommands.Add("zuzu_quotes", I18n.Get("cmd.quotes.help"), WithSimulation(Quotes));
        _helper.ConsoleCommands.Add("zuzu_portfolio", I18n.Get("cmd.portfolio.help"), WithSimulation(PortfolioCmd));
        _helper.ConsoleCommands.Add("zuzu_terminal", I18n.Get("cmd.terminal.help"), WithSimulation(TerminalCmd));
        _helper.ConsoleCommands.Add("zuzu_trade", I18n.Get("cmd.trade.help"), WithSimulation(TradeCmd));
        _helper.ConsoleCommands.Add("zuzu_place_phone", I18n.Get("cmd.place-phone.help"), WithSimulation(PlacePhone));
        _helper.ConsoleCommands.Add("zuzu_rates", I18n.Get("cmd.rates.help"), WithSimulation(Rates));
        _helper.ConsoleCommands.Add("zuzu_bonds", I18n.Get("cmd.bonds.help"), WithSimulation(Bonds));
        _helper.ConsoleCommands.Add("zuzu_default", I18n.Get("cmd.default.help"), WithSimulation(DefaultCmd));
        _helper.ConsoleCommands.Add("zuzu_futures", I18n.Get("cmd.futures.help"), WithSimulation(FuturesCmd));
        _helper.ConsoleCommands.Add("zuzu_options", I18n.Get("cmd.options.help"), WithSimulation(OptionsCmd));
        _helper.ConsoleCommands.Add("zuzu_silo", I18n.Get("cmd.silo.help"), WithSimulation(SiloCmd));
        _helper.ConsoleCommands.Add("zuzu_margin_call", I18n.Get("cmd.margin-call.help"), WithSimulation(MarginCallCmd));
        _helper.ConsoleCommands.Add("zuzu_credit", I18n.Get("cmd.credit.help"), WithSimulation(CreditCmd));
        _helper.ConsoleCommands.Add("zuzu_legal", I18n.Get("cmd.legal.help"), WithSimulation(LegalCmd));
        _helper.ConsoleCommands.Add("zuzu_debt", I18n.Get("cmd.debt.help"), WithSimulation(DebtCmd));
        _helper.ConsoleCommands.Add("zuzu_companies", I18n.Get("cmd.companies.help"), WithSimulation(CompaniesCmd));
        _helper.ConsoleCommands.Add("zuzu_company", I18n.Get("cmd.company.help"), WithSimulation(CompanyCmd));
        _helper.ConsoleCommands.Add("zuzu_exports", I18n.Get("cmd.exports.help"), WithSimulation(ExportsCmd));
        _helper.ConsoleCommands.Add("zuzu_company_eps", I18n.Get("cmd.company-eps.help"), WithSimulation(CompanyEpsCmd));
        _helper.ConsoleCommands.Add("zuzu_unlock_companies", I18n.Get("cmd.unlock-companies.help"), WithSimulation(UnlockCompaniesCmd));
    }

    private Action<string, string[]> WithSimulation(Action<EconomySimulation, string[]> action) => (_, args) =>
    {
        EconomySimulation? simulation = _getSimulation();
        if (!Context.IsWorldReady || simulation is null)
        {
            _monitor.Log(I18n.Get("cmd.no-game"), LogLevel.Warn);
            return;
        }

        action(simulation, args);
    };

    private void Status(EconomySimulation sim, string[] args)
    {
        EconomicDate date = sim.CurrentDate;
        decimal cash = sim.Ledger.GetBalance(LedgerEntities.Jugador, SystemAccounts.Caja);
        long gold = MoneyBridge.PlayerGold;
        string yes = I18n.Get("common.yes");
        string no = I18n.Get("common.no");

        var sb = new StringBuilder();
        sb.AppendLine(I18n.Get("cmd.status.title"));
        sb.AppendLine(I18n.Get("cmd.status.date", new { date = GameDateBridge.Format(I18n, date) }));
        sb.AppendLine(I18n.Get("cmd.status.business-day", new
        {
            business = date.IsBusinessDay ? yes : no,
            open = MarketHours.IsOpen(date, StardewValley.Game1.timeOfDay) ? yes : no,
        }));
        sb.AppendLine(I18n.Get("cmd.status.period", new { period = GameDateBridge.Format(I18n, date.FiscalPeriod) }));
        sb.AppendLine(I18n.Get("cmd.status.difficulty", new { difficulty = sim.Difficulty.Name }));
        sb.AppendLine(I18n.Get("cmd.status.seed", new { seed = sim.Seed, since = GameDateBridge.Format(I18n, sim.CreatedOn) }));
        sb.AppendLine(I18n.Get("cmd.status.cash", new { cash = Money(cash), gold = gold, diff = Money(gold - cash) }));
        sb.Append(I18n.Get("cmd.status.entries", new { count = sim.Ledger.Entries.Count }));
        _monitor.Log(sb.ToString(), LogLevel.Info);
    }

    private void LedgerCmd(EconomySimulation sim, string[] args)
    {
        int count = args.Length > 0 && int.TryParse(args[0], out int n) && n > 0 ? n : 10;
        IReadOnlyList<JournalEntry> entries = sim.Ledger.GetRecentEntries(count);
        if (entries.Count == 0)
        {
            _monitor.Log(I18n.Get("cmd.ledger.empty"), LogLevel.Info);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine(I18n.Get("cmd.ledger.title", new { count = entries.Count }));
        foreach (JournalEntry entry in entries.Reverse())
        {
            sb.AppendLine($"#{entry.Id} · {GameDateBridge.Format(I18n, entry.Date)} · {entry.EntityId} · [{entry.Source}] {Describe(entry)}");
            foreach (JournalLine line in entry.Lines)
            {
                string name = sim.Ledger.Chart.Get(line.AccountCode).Name;
                string column = line.Debit > 0
                    ? $"    {I18n.Get("ledger.debit"),-6} {Money(line.Debit),14}"
                    : $"    {I18n.Get("ledger.credit"),-6} {string.Empty,14} {Money(line.Credit),14}";
                sb.AppendLine($"{column}   {line.AccountCode} {name}");
            }
        }

        _monitor.Log(sb.ToString().TrimEnd(), LogLevel.Info);
    }

    private void Balance(EconomySimulation sim, string[] args)
    {
        string entity = args.Length > 0 ? args[0] : LedgerEntities.Jugador;
        if (!sim.Ledger.HasEntity(entity))
        {
            _monitor.Log(I18n.Get("cmd.balance.unknown-entity", new { entity, entities = string.Join(", ", sim.Ledger.Entities) }), LogLevel.Warn);
            return;
        }

        IReadOnlyList<TrialBalanceRow> rows = sim.Ledger.GetTrialBalance(entity);
        var sb = new StringBuilder();
        sb.AppendLine(I18n.Get("cmd.balance.title", new { entity }));
        sb.AppendLine($"{I18n.Get("ledger.account"),-44} {I18n.Get("ledger.debits"),14} {I18n.Get("ledger.credits"),14} {I18n.Get("ledger.balance"),14}");
        foreach (TrialBalanceRow row in rows)
        {
            string label = $"{row.Account.Code} {row.Account.Name}";
            if (label.Length > 44)
            {
                label = label[..43] + "…";
            }

            sb.AppendLine($"{label,-44} {Money(row.Debits),14} {Money(row.Credits),14} {Money(row.Balance),14}");
        }

        decimal debits = rows.Sum(r => r.Debits);
        decimal credits = rows.Sum(r => r.Credits);
        sb.Append($"{I18n.Get("ledger.totals"),-44} {Money(debits),14} {Money(credits),14}");
        sb.Append(debits == credits ? $"   ✔ {I18n.Get("ledger.balanced")}" : $"   ✘ {I18n.Get("ledger.unbalanced")}");
        _monitor.Log(sb.ToString(), debits == credits ? LogLevel.Info : LogLevel.Error);
    }

    private void Rng(EconomySimulation sim, string[] args)
    {
        if (args.Length == 0)
        {
            _monitor.Log(I18n.Get("cmd.rng.usage"), LogLevel.Info);
            return;
        }

        int count = args.Length > 1 && int.TryParse(args[1], out int n) && n > 0 ? Math.Min(n, 50) : 5;
        DeterministicRandom rng = sim.Random(args[0]);
        string values = string.Join(", ", Enumerable.Range(0, count).Select(_ => rng.NextDouble().ToString("0.000000", CultureInfo.InvariantCulture)));
        _monitor.Log(I18n.Get("cmd.rng.result", new { stream = args[0], date = GameDateBridge.Format(I18n, sim.CurrentDate), values }), LogLevel.Info);
    }

    private void Reset(EconomySimulation sim, string[] args)
    {
        if (!_config.DebugMode)
        {
            _monitor.Log(I18n.Get("cmd.debug-only", new { command = "zuzu_reset" }), LogLevel.Warn);
            return;
        }

        if (args.Length == 0 || args[0] != "confirm")
        {
            _monitor.Log(I18n.Get("cmd.reset.confirm"), LogLevel.Warn);
            return;
        }

        _reset();
        _monitor.Log(I18n.Get("cmd.reset.done"), LogLevel.Info);
    }

    private void Prices(EconomySimulation sim, string[] args)
    {
        if (args.Length > 0)
        {
            CommodityDefinition? commodity = sim.Data.FindCommodity(args[0]);
            if (commodity is null)
            {
                _monitor.Log(I18n.Get("cmd.prices.unknown", new { id = args[0], ids = string.Join(", ", sim.Data.Commodities.Select(c => c.Id)) }), LogLevel.Warn);
                return;
            }

            PriceDetail(sim, commodity);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine(I18n.Get("cmd.prices.title", new { date = GameDateBridge.Format(I18n, sim.CurrentDate), ipc = Formatting.Number(I18n, sim.Inflation.Index) }));
        sb.AppendLine($"{I18n.Get("cmd.prices.col.commodity"),-22} {I18n.Get("cmd.prices.col.price"),10} {I18n.Get("cmd.prices.col.change"),9} {I18n.Get("cmd.prices.col.real"),8} {I18n.Get("cmd.prices.col.stock"),8}");
        foreach (CommodityDefinition c in sim.Data.Commodities)
        {
            CommodityState s = sim.Market.Get(c.Id);
            string price = ReferencePrice(c) is int vanilla ? Formatting.Gold(I18n, vanilla * s.Multiplier) : "-";
            sb.AppendLine($"{CommodityName(c),-22} {price,10} {Formatting.Percent(I18n, s.DailyChange, signed: true),9} {Formatting.Number(I18n, s.RealMultiplier, "0.00"),8} {s.Stock,8:0}");
        }

        sb.Append(I18n.Get("cmd.prices.footer"));
        _monitor.Log(sb.ToString(), LogLevel.Info);
    }

    private void PriceDetail(EconomySimulation sim, CommodityDefinition c)
    {
        CommodityState s = sim.Market.Get(c.Id);
        var sb = new StringBuilder();
        sb.AppendLine($"=== {CommodityName(c)} ({c.Id}) · {c.Sector} ===");
        if (ReferencePrice(c) is int vanilla)
        {
            sb.AppendLine(I18n.Get("cmd.prices.detail.price", new { vanilla = Formatting.Gold(I18n, vanilla), price = Formatting.Gold(I18n, vanilla * s.Multiplier) }));
        }

        sb.AppendLine(I18n.Get("cmd.prices.detail.multipliers", new
        {
            total = Formatting.Number(I18n, s.Multiplier),
            real = Formatting.Number(I18n, s.RealMultiplier),
            ipc = Formatting.Number(I18n, sim.Inflation.Index),
            season = Formatting.Number(I18n, c.SeasonFactor(sim.CurrentDate.Season), "0.00"),
        }));
        sb.AppendLine(I18n.Get("cmd.prices.detail.supply", new { stock = s.Stock.ToString("0"), pending = s.PendingSales.ToString("0") }));
        sb.AppendLine(I18n.Get("cmd.prices.detail.history"));
        foreach (PricePoint p in s.History.Skip(Math.Max(0, s.History.Count - 10)))
        {
            sb.AppendLine($"  {GameDateBridge.Format(I18n, p.Date),-28} {Formatting.Number(I18n, p.Multiplier),8}   {I18n.Get("cmd.prices.detail.sold", new { units = p.UnitsSold.ToString("0") })}");
        }

        _monitor.Log(sb.ToString().TrimEnd(), LogLevel.Info);
    }

    private void InflationCmd(EconomySimulation sim, string[] args)
    {
        InflationState state = sim.Inflation.State;
        var sb = new StringBuilder();
        sb.AppendLine(I18n.Get("cmd.inflation.title"));
        sb.AppendLine(I18n.Get("cmd.inflation.current", new
        {
            ipc = Formatting.Number(I18n, state.Index),
            rate = Formatting.Percent(I18n, state.PeriodRate),
            accumulated = Formatting.Percent(I18n, state.Index - 1),
        }));
        if (state.History.Count == 0)
        {
            sb.Append(I18n.Get("cmd.inflation.no-history"));
        }

        foreach (InflationRecord r in state.History)
        {
            sb.AppendLine($"  {GameDateBridge.Format(I18n, r.Period),-24} {Formatting.Percent(I18n, r.Rate),8}   IPC {Formatting.Number(I18n, r.IndexAtClose)}");
        }

        _monitor.Log(sb.ToString().TrimEnd(), LogLevel.Info);
    }

    private void EventsCmd(EconomySimulation sim, string[] args)
    {
        var sb = new StringBuilder();
        sb.AppendLine(I18n.Get("cmd.events.active"));
        if (sim.Events.Active.Count == 0)
        {
            sb.AppendLine("  " + I18n.Get("cmd.events.none"));
        }

        foreach (ActiveEvent a in sim.Events.Active)
        {
            sb.AppendLine($"  [{a.EventId}] {I18n.Get($"event.{a.EventId}.headline")} — {I18n.Get("cmd.events.until", new { date = GameDateBridge.Format(I18n, a.End) })}");
        }

        sb.AppendLine(I18n.Get("cmd.events.news"));
        foreach (NewsItem n in sim.Events.State.News.Skip(Math.Max(0, sim.Events.State.News.Count - 10)))
        {
            string text = NewsText.Headline(I18n, sim, n);
            sb.AppendLine($"  {GameDateBridge.Format(I18n, n.Date),-28} {text}");
        }

        if (_config.DebugMode)
        {
            sb.Append(I18n.Get("cmd.events.ids", new { ids = string.Join(", ", sim.Data.Events.Select(e => e.Id)) }));
        }

        _monitor.Log(sb.ToString().TrimEnd(), LogLevel.Info);
    }

    private void ForceEvent(EconomySimulation sim, string[] args)
    {
        if (!_config.DebugMode)
        {
            _monitor.Log(I18n.Get("cmd.debug-only", new { command = "zuzu_event" }), LogLevel.Warn);
            return;
        }

        if (args.Length == 0)
        {
            _monitor.Log(I18n.Get("cmd.event.usage", new { ids = string.Join(", ", sim.Data.Events.Select(e => e.Id)) }), LogLevel.Info);
            return;
        }

        EventDefinition? e = sim.ForceEvent(args[0]);
        if (e is null)
        {
            _monitor.Log(I18n.Get("cmd.event.unknown", new { id = args[0] }), LogLevel.Warn);
            return;
        }

        _news.Announce(e);
        _monitor.Log(I18n.Get("cmd.event.done", new { id = e.Id }), LogLevel.Info);
    }

    private void TaxesCmd(EconomySimulation sim, string[] args)
    {
        TaxEngine taxes = sim.Taxes;
        TaxState s = taxes.State;
        var sb = new StringBuilder();
        sb.AppendLine(I18n.Get("cmd.taxes.title"));
        if (!taxes.Started)
        {
            _monitor.Log(I18n.Get("cmd.taxes.not-started"), LogLevel.Info);
            return;
        }

        sb.AppendLine(I18n.Get("cmd.taxes.regime", new
        {
            regime = TaxText.Regime(I18n, s.Regime),
            category = s.Regime == TaxRegime.Monotributo ? s.Category : "-",
            since = s.RegisteredOn is { } registered ? GameDateBridge.Format(I18n, registered) : "-",
        }));
        decimal annual = s.RevenueHistory.Skip(Math.Max(0, s.RevenueHistory.Count - 4)).Sum(r => r.NetSales);
        sb.AppendLine(I18n.Get("cmd.taxes.revenue", new
        {
            period = Formatting.Gold(I18n, (double)s.Current.NetSales),
            annual = Formatting.Gold(I18n, (double)annual),
            purchases = Formatting.Gold(I18n, (double)s.Current.NetPurchases),
        }));
        if (s.Regime == TaxRegime.ResponsableInscripto)
        {
            sb.AppendLine(I18n.Get("cmd.taxes.iva", new
            {
                debit = Money(sim.Ledger.GetBalance(LedgerEntities.Jugador, SystemAccounts.IvaDebitoFiscal)),
                credit = Money(sim.Ledger.GetBalance(LedgerEntities.Jugador, SystemAccounts.IvaCreditoFiscal)),
            }));
        }

        List<TaxObligation> unpaid = taxes.Unpaid.OrderBy(o => o.DueDate).ThenBy(o => o.Id).ToList();
        sb.AppendLine(I18n.Get("cmd.taxes.unpaid", new { count = unpaid.Count, total = Formatting.Gold(I18n, unpaid.Sum(o => o.Total)) }));
        foreach (TaxObligation o in unpaid)
        {
            sb.AppendLine($"  #{o.Id} [{TaxText.Jurisdiction(I18n, o.Jurisdiction)}] {TaxText.Line(I18n, o)} · {TaxText.Status(I18n, o.Status)}");
        }

        int recentPaid = 5;
        List<TaxObligation> paid = s.Obligations.Where(o => o.IsPaid).Skip(Math.Max(0, s.Obligations.Count(o => o.IsPaid) - recentPaid)).ToList();
        if (paid.Count > 0)
        {
            sb.AppendLine(I18n.Get("cmd.taxes.paid"));
            foreach (TaxObligation o in paid)
            {
                sb.AppendLine($"  #{o.Id} {TaxText.Tax(I18n, o.Kind)} {GameDateBridge.Format(I18n, o.Period)} — {Formatting.Gold(I18n, o.PaidAmount)} ({GameDateBridge.Format(I18n, o.PaidOn!.Value)})");
            }
        }

        sb.Append(I18n.Get("cmd.taxes.footer", new { autodebit = _config.AutoDebit ? I18n.Get("common.yes") : I18n.Get("common.no") }));
        _monitor.Log(sb.ToString(), LogLevel.Info);
    }

    private void Results(EconomySimulation sim, string[] args)
    {
        IReadOnlyList<TrialBalanceRow> rows = sim.Ledger.GetTrialBalance(LedgerEntities.Jugador);
        var income = rows.Where(r => r.Account.Type == AccountType.Ingreso).ToList();
        var expenses = rows.Where(r => r.Account.Type == AccountType.Egreso).ToList();
        var sb = new StringBuilder();
        sb.AppendLine(I18n.Get("cmd.results.title", new { since = GameDateBridge.Format(I18n, sim.CreatedOn) }));
        sb.AppendLine(I18n.Get("cmd.results.income"));
        foreach (TrialBalanceRow r in income)
        {
            sb.AppendLine($"  {r.Account.Name,-44} {Money(r.Balance),14}");
        }

        sb.AppendLine(I18n.Get("cmd.results.expenses"));
        foreach (TrialBalanceRow r in expenses)
        {
            sb.AppendLine($"  {r.Account.Name,-44} {Money(-r.Balance),14}");
        }

        decimal result = income.Sum(r => r.Balance) - expenses.Sum(r => r.Balance);
        sb.Append(I18n.Get("cmd.results.total", new { result = Money(result) }));
        _monitor.Log(sb.ToString(), LogLevel.Info);
    }

    private void Here(EconomySimulation sim, string[] args)
    {
        var tile = Game1.player.Tile;
        var facing = Game1.player.GetGrabTile();
        _monitor.Log(I18n.Get("cmd.here.result", new
        {
            location = Game1.currentLocation?.NameOrUniqueName ?? "-",
            x = (int)tile.X,
            y = (int)tile.Y,
            fx = (int)facing.X,
            fy = (int)facing.Y,
        }), LogLevel.Info);
    }

    private void PayCmd(EconomySimulation sim, string[] args)
    {
        if (!_config.DebugMode)
        {
            _monitor.Log(I18n.Get("cmd.debug-only", new { command = "zuzu_pay" }), LogLevel.Warn);
            return;
        }

        List<TaxObligation> unpaid = sim.Taxes.Unpaid.OrderBy(o => o.DueDate).ThenBy(o => o.Id).ToList();
        IEnumerable<TaxObligation> selected = args.Length > 0 && long.TryParse(args[0], out long id)
            ? unpaid.Where(o => o.Id == id)
            : unpaid;
        _municipalidad.Pay(sim, selected, automatic: false);
        _monitor.Log(I18n.Get("cmd.pay.done"), LogLevel.Info);
    }

    private void RegimeCmd(EconomySimulation sim, string[] args)
    {
        if (!_config.DebugMode)
        {
            _monitor.Log(I18n.Get("cmd.debug-only", new { command = "zuzu_regime" }), LogLevel.Warn);
            return;
        }

        TaxRegime? regime = args.Length == 0 ? null : args[0].ToLowerInvariant() switch
        {
            "ri" => TaxRegime.ResponsableInscripto,
            "mono" => TaxRegime.Monotributo,
            _ => null,
        };
        if (regime is null)
        {
            _monitor.Log(I18n.Get("cmd.regime.usage"), LogLevel.Info);
            return;
        }

        sim.Taxes.SetRegime(regime.Value, sim.CurrentDate);
        _monitor.Log(I18n.Get("cmd.regime.done", new { regime = TaxText.Regime(I18n, regime.Value) }), LogLevel.Info);
    }

    private void Quotes(EconomySimulation sim, string[] args)
    {
        int time = Game1.timeOfDay;
        var sb = new StringBuilder();
        sb.AppendLine(I18n.Get("cmd.quotes.title", new
        {
            date = GameDateBridge.Format(I18n, sim.CurrentDate),
            time = Game1.getTimeOfDayString(time),
            merzu = Formatting.Number(I18n, sim.Stocks.MerzuAt(time), "#,0.00"),
            open = Broker.IsMarketOpen(sim.CurrentDate, time) ? I18n.Get("terminal.market-open") : I18n.Get("terminal.market-closed"),
        }));
        foreach (CompanyDefinition c in sim.Stocks.Companies)
        {
            StockState s = sim.Stocks.Get(c.Ticker);
            double price = sim.Stocks.PriceAt(c.Ticker, time);
            double change = s.PreviousClose > 0 ? (price / s.PreviousClose) - 1 : 0;
            sb.AppendLine($"  {c.Ticker,-5} {c.Name,-32} {price,10:0.00}g {Formatting.Percent(I18n, change, signed: true),8}");
        }

        foreach (LetraSeries l in sim.Broker.ActiveLetras(sim.CurrentDate))
        {
            sb.AppendLine($"  {l.Id,-12} {GameDateBridge.Format(I18n, l.Maturity),-26} {sim.Broker.LetraPrice(l, sim.CurrentDate),10:0.00}g {Formatting.Percent(I18n, sim.Broker.LetraRate(l)),8}");
        }

        sb.Append(I18n.Get("cmd.quotes.rates", new { reference = Formatting.Percent(I18n, sim.Broker.ReferenceRate), caucion = Formatting.Percent(I18n, sim.Broker.CaucionRate) }));
        _monitor.Log(sb.ToString(), LogLevel.Info);
    }

    private void PortfolioCmd(EconomySimulation sim, string[] args)
    {
        if (!sim.Broker.IsOpen)
        {
            _monitor.Log(I18n.Get("error.NoAccount"), LogLevel.Info);
            return;
        }

        int time = Game1.timeOfDay;
        var sb = new StringBuilder();
        sb.AppendLine(I18n.Get("cmd.portfolio.title"));
        foreach ((string ticker, Holding h) in sim.Broker.State.Stocks)
        {
            sb.AppendLine($"  {ticker,-12} {h.Quantity,8} × {sim.Stocks.PriceAt(ticker, time),10:0.00}g  (costo {h.AverageCost:0.00}g)");
        }

        foreach ((string id, Holding h) in sim.Broker.State.Letras)
        {
            double price = sim.Broker.FindLetra(id) is { } l ? sim.Broker.LetraPrice(l, sim.CurrentDate) : 0;
            sb.AppendLine($"  {id,-12} {h.Quantity,8} × {price,10:0.00}g  (costo {h.AverageCost:0.00}g)");
        }

        foreach (Caucion c in sim.Broker.State.Cauciones)
        {
            sb.AppendLine($"  Caución #{c.Id}: {Money(c.Principal)} + {Money(c.Interest)} → {GameDateBridge.Format(I18n, c.Maturity)}");
        }

        sb.Append(I18n.Get("cmd.portfolio.totals", new
        {
            cash = Money(sim.Broker.Cash),
            value = Money(sim.Broker.PortfolioValue(sim.CurrentDate, time)),
        }));
        _monitor.Log(sb.ToString(), LogLevel.Info);
    }

    private void TerminalCmd(EconomySimulation sim, string[] args)
    {
        if (!_config.DebugMode)
        {
            _monitor.Log(I18n.Get("cmd.debug-only", new { command = "zuzu_terminal" }), LogLevel.Warn);
            return;
        }

        _openTerminal();
    }

    private void TradeCmd(EconomySimulation sim, string[] args)
    {
        if (!_config.DebugMode)
        {
            _monitor.Log(I18n.Get("cmd.debug-only", new { command = "zuzu_trade" }), LogLevel.Warn);
            return;
        }

        if (args.Length < 3 || !long.TryParse(args[2], out long quantity) || args[0] is not ("buy" or "sell"))
        {
            _monitor.Log(I18n.Get("cmd.trade.usage"), LogLevel.Info);
            return;
        }

        TradeSide side = args[0] == "buy" ? TradeSide.Buy : TradeSide.Sell;
        TradeQuote q = sim.Broker.Execute(args[1], side, quantity, sim.CurrentDate, Game1.timeOfDay);
        _monitor.Log(q.IsValid
            ? I18n.Get("cmd.trade.done", new { instrument = q.Instrument, quantity = q.Quantity, total = Money(q.Total) })
            : I18n.Get($"error.{q.Error}"), q.IsValid ? LogLevel.Info : LogLevel.Warn);
    }

    private void Rates(EconomySimulation sim, string[] args)
    {
        Broker b = sim.Broker;
        var sb = new StringBuilder();
        sb.AppendLine(I18n.Get("cmd.rates.title"));
        sb.AppendLine(I18n.Get("cmd.rates.policy", new
        {
            rate = Formatting.Percent(I18n, b.ReferenceRate),
            caucion = Formatting.Percent(I18n, b.CaucionRate),
            risk = Formatting.Number(I18n, b.CountryRiskBps, "#,0"),
            rating = b.Rating(CreditEngine.Sovereign),
        }));
        foreach (int days in new[] { 28, 56, 112, 224, 448, 896, 1344 })
        {
            sb.AppendLine($"  {days,5} d   {Formatting.Percent(I18n, b.Yield(days, CreditEngine.Sovereign)),8}");
        }

        sb.AppendLine(I18n.Get("cmd.rates.history"));
        foreach (var d in sim.CentralBank.State.History.Skip(Math.Max(0, sim.CentralBank.State.History.Count - 6)))
        {
            sb.AppendLine($"  {GameDateBridge.Format(I18n, d.Date),-28} {Formatting.Percent(I18n, d.Rate),8} ({Formatting.Percent(I18n, d.Change, signed: true)})");
        }

        sb.Append(I18n.Get("cmd.rates.ratings", new { ratings = string.Join(", ", sim.Credit.Issuers.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value.Rating}")) }));
        _monitor.Log(sb.ToString(), LogLevel.Info);
    }

    private void Bonds(EconomySimulation sim, string[] args)
    {
        var sb = new StringBuilder();
        sb.AppendLine(I18n.Get("cmd.bonds.title"));
        foreach (BondSeries bond in sim.Broker.ActiveBonds(true).Concat(sim.Broker.ActiveBonds(false)))
        {
            bool defaulted = sim.Broker.IsDefaulted(bond);
            sb.AppendLine($"  {bond.Id,-10} {bond.Issuer,-9} {(defaulted ? "D" : sim.Broker.Rating(bond.Issuer)),-4} {bond.Type,-9} cupón {Formatting.Percent(I18n, bond.CouponRate),7}  vence {GameDateBridge.Format(I18n, bond.Maturity),-26} TIR {(defaulted ? "-" : Formatting.Percent(I18n, sim.Broker.BondYield(bond, sim.CurrentDate))),7}  precio {sim.Broker.BondPrice(bond, sim.CurrentDate),7:0.00}g");
        }

        _monitor.Log(sb.ToString().TrimEnd(), LogLevel.Info);
    }

    private void DefaultCmd(EconomySimulation sim, string[] args)
    {
        if (!_config.DebugMode)
        {
            _monitor.Log(I18n.Get("cmd.debug-only", new { command = "zuzu_default" }), LogLevel.Warn);
            return;
        }

        if (args.Length == 0)
        {
            _monitor.Log(I18n.Get("cmd.default.usage"), LogLevel.Info);
            return;
        }

        _monitor.Log(sim.ForceDefault(args[0])
            ? I18n.Get("cmd.default.done", new { issuer = args[0] })
            : I18n.Get("cmd.default.unknown", new { issuer = args[0] }), LogLevel.Info);
    }

    private void FuturesCmd(EconomySimulation sim, string[] args)
    {
        var sb = new StringBuilder();
        sb.AppendLine(I18n.Get("cmd.futures.title"));
        IEnumerable<FuturesContract> contracts = sim.Broker.Derivatives.Futures.OrderBy(f => f.CommodityId).ThenBy(f => f.Expiry);
        if (args.Length > 0)
        {
            contracts = contracts.Where(f => string.Equals(f.CommodityId, args[0], StringComparison.OrdinalIgnoreCase));
        }

        foreach (FuturesContract f in contracts)
        {
            long pos = sim.Broker.FuturesPositionFor(f.Id)?.Quantity ?? 0;
            sb.AppendLine($"  {f.Id,-14} {GameDateBridge.Format(I18n, f.Expiry),-28} {sim.Broker.FuturePrice(f, sim.CurrentDate),10:0.00}g × {sim.Broker.ContractSize(f),4}   {(pos == 0 ? "" : pos.ToString("+0;-0"))}");
        }

        sb.Append(I18n.Get("cmd.futures.margin", new
        {
            balance = Money(sim.Broker.MarginBalance),
            required = Money(sim.Broker.RequiredMargin(sim.CurrentDate, Game1.timeOfDay)),
        }));
        _monitor.Log(sb.ToString(), LogLevel.Info);
    }

    private void OptionsCmd(EconomySimulation sim, string[] args)
    {
        string ticker = args.Length > 0 ? args[0].ToUpperInvariant() : sim.Stocks.Companies[0].Ticker;
        var sb = new StringBuilder();
        sb.AppendLine(I18n.Get("cmd.options.title", new { ticker, price = sim.Stocks.PriceAt(ticker, Game1.timeOfDay).ToString("0.00") }));
        foreach (OptionSeries o in sim.Broker.Derivatives.Options.Where(o => o.Ticker == ticker).OrderBy(o => o.Expiry).ThenBy(o => o.Strike).ThenBy(o => o.Type))
        {
            long pos = sim.Broker.OptionPositionFor(o.Id)?.Quantity ?? 0;
            sb.AppendLine($"  {o.Id,-18} prima {sim.Broker.OptionPremium(o, sim.CurrentDate, Game1.timeOfDay),8:0.00}g  delta {sim.Broker.OptionDelta(o, sim.CurrentDate, Game1.timeOfDay),5:0.00}  {(pos == 0 ? "" : pos.ToString("+0;-0"))}");
        }

        _monitor.Log(sb.ToString().TrimEnd(), LogLevel.Info);
    }

    private void SiloCmd(EconomySimulation sim, string[] args)
    {
        if (args.Length > 0 && args[0] == "open")
        {
            if (!_config.DebugMode)
            {
                _monitor.Log(I18n.Get("cmd.debug-only", new { command = "zuzu_silo open" }), LogLevel.Warn);
                return;
            }

            _openSilo();
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine(I18n.Get("cmd.silo.title"));
        if (sim.Broker.Silo.Count == 0)
        {
            sb.Append(I18n.Get("cmd.silo.empty"));
        }

        foreach (SiloItem i in sim.Broker.Silo)
        {
            sb.AppendLine($"  {i.ItemId,-6} calidad {i.Quality}  × {i.Stack}  (precio base {i.BasePrice}g)");
        }

        _monitor.Log(sb.ToString().TrimEnd(), LogLevel.Info);
    }

    private void MarginCallCmd(EconomySimulation sim, string[] args)
    {
        if (!_config.DebugMode)
        {
            _monitor.Log(I18n.Get("cmd.debug-only", new { command = "zuzu_margin_call" }), LogLevel.Warn);
            return;
        }

        if (args.Length < 2 || !double.TryParse(args[1], System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out double shock))
        {
            _monitor.Log(I18n.Get("cmd.margin-call.usage"), LogLevel.Info);
            return;
        }

        var notices = sim.ForceMarginTest(args[0], shock);
        _monitor.Log(I18n.Get("cmd.margin-call.done", new
        {
            notices = notices.Count == 0 ? "-" : string.Join(", ", notices.Select(n => n.Kind)),
            balance = Money(sim.Broker.MarginBalance),
        }), LogLevel.Info);
    }

    private void CreditCmd(EconomySimulation sim, string[] args)
    {
        var legal = sim.Legal;
        var sb = new StringBuilder();
        sb.AppendLine(I18n.Get("cmd.credit.title"));
        sb.AppendLine(I18n.Get("cmd.credit.summary", new
        {
            rating = legal.Rating,
            score = legal.State.Score.ToString("0"),
            limit = Money(legal.LoanLimit),
            rate = Formatting.Percent(I18n, legal.LoanRate),
            worth = Money(legal.NetWorth),
        }));
        foreach (var loan in legal.State.Loans)
        {
            sb.AppendLine($"  Préstamo #{loan.Id}: saldo {Money(loan.Outstanding)} · cuota {loan.Installment}g · {loan.InstallmentsIssued}/{loan.TermSeasons} · tasa {Formatting.Percent(I18n, loan.Rate)}");
        }

        foreach (var repo in sim.Broker.State.Repos)
        {
            sb.AppendLine($"  Caución tomadora #{repo.Id}: {Money(repo.Principal)} + {Money(repo.Interest)} → {GameDateBridge.Format(I18n, repo.Maturity)}");
        }

        _monitor.Log(sb.ToString().TrimEnd(), LogLevel.Info);
    }

    private void LegalCmd(EconomySimulation sim, string[] args)
    {
        var s = sim.Legal.State;
        string Date(ZuzuMarket.Core.Time.EconomicDate? d) => d is { } x ? GameDateBridge.Format(I18n, x) : "-";
        _monitor.Log(I18n.Get("cmd.legal.summary", new
        {
            debt = Money(sim.Legal.DebtInCollection),
            collection = Date(s.CollectionSince),
            embargo = Date(s.EmbargoSince),
            concurso = s.ConcursoActive ? I18n.Get("common.yes") : I18n.Get("common.no"),
            inhabilitado = Date(s.InhabilitadoUntil),
            bankruptcies = s.Bankruptcies,
            embargoes = string.Join(", ", sim.Difficulty.Embargoes),
        }), LogLevel.Info);
    }

    private void DebtCmd(EconomySimulation sim, string[] args)
    {
        if (!_config.DebugMode)
        {
            _monitor.Log(I18n.Get("cmd.debug-only", new { command = "zuzu_debt" }), LogLevel.Warn);
            return;
        }

        if (args.Length == 0 || !long.TryParse(args[0], out long amount) || amount <= 0)
        {
            _monitor.Log(I18n.Get("cmd.debt.usage"), LogLevel.Info);
            return;
        }

        sim.CreateTestDebt(amount);
        _monitor.Log(I18n.Get("cmd.debt.done", new { amount = Money(amount) }), LogLevel.Info);
    }

    private void CompaniesCmd(EconomySimulation sim, string[] args)
    {
        var sb = new StringBuilder();
        sb.AppendLine(I18n.Get("cmd.companies.title"));
        foreach (ZuzuMarket.Core.Companies.CompanyTypeDefinition t in sim.Data.PlayerCompanies.Types)
        {
            bool unlocked = sim.Companies.State.UnlockAll || ZuzuMarket.Game.Bridges.CompanyUnlocks.IsUnlocked(t);
            sb.AppendLine($"  {ZuzuMarket.Game.Content.CompanyMail.TypeName(I18n, t.Id)}: {(unlocked ? I18n.Get("cmd.companies.unlocked") : I18n.Get($"company.unlock.{t.Unlock}", new { machines = ZuzuMarket.Game.Bridges.CompanyUnlocks.MachinesNeeded }))}");
        }

        foreach (ZuzuMarket.Core.Companies.CompanyState co in sim.Companies.State.Companies)
        {
            string status = co.Active ? I18n.Get("cmd.companies.active") : co.Bankrupt ? I18n.Get("cmd.companies.bankrupt") : I18n.Get("cmd.companies.dissolved");
            sb.AppendLine(I18n.Get("cmd.companies.line", new
            {
                id = co.Id,
                type = ZuzuMarket.Game.Content.CompanyMail.TypeName(I18n, co.Type),
                form = co.Form,
                cuit = co.Cuit,
                status,
                rating = co.Rating,
                cash = Money(sim.Companies.Cash(co)),
                revenue = Money(sim.Companies.AnnualRevenue(co)),
            }));
        }

        _monitor.Log(sb.ToString().TrimEnd(), LogLevel.Info);
    }

    private void CompanyCmd(EconomySimulation sim, string[] args)
    {
        if (args.Length == 0 || sim.Companies.Find(args[0]) is not { } co)
        {
            _monitor.Log(I18n.Get("cmd.company.usage"), LogLevel.Info);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"=== {co.Id} · {ZuzuMarket.Game.Content.CompanyMail.TypeName(I18n, co.Type)} {co.Form} · CUIT {co.Cuit} ===");
        sb.AppendLine(I18n.Get("cmd.company.summary", new
        {
            cash = Money(sim.Companies.Cash(co)),
            capital = Money(sim.Companies.CapitalOf(co)),
            retained = Money(sim.Companies.RetainedEarnings(co)),
            distributable = Money(sim.Companies.Distributable(co)),
            contributed = Money(co.Contributed),
            dividends = Money(co.DividendsPaid),
        }));
        if (co.Ticker is { } ticker)
        {
            sb.AppendLine(I18n.Get("cmd.company.market", new
            {
                ticker,
                eps = sim.Companies.MarketEps(co).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
                fundamental = Money(sim.Companies.FundamentalPrice(co)),
                listed = co.Listing is { } l
                    ? I18n.Get("cmd.company.listed", new { price = Money(sim.Companies.MarketPrice(co)), pct = Formatting.Percent(I18n, (double)l.PlayerFraction), shares = l.TotalShares })
                    : I18n.Get("cmd.company.not-listed"),
                bonds = Money(sim.Companies.BondsOutstanding(co)),
                limit = Money(sim.Companies.BondLimit(co)),
            }));
            foreach (BondSeries bond in sim.Broker.ActiveBonds(false).Where(b => b.Issuer == ticker))
            {
                sb.AppendLine($"  {bond.Id}: {bond.IssuedUnits * 100}g VN · cupón {Formatting.Percent(I18n, bond.CouponRate)} · vence {GameDateBridge.Format(I18n, bond.Maturity)}");
            }
        }

        foreach (TrialBalanceRow row in sim.Ledger.GetTrialBalance(co.Id).Where(r => r.Balance != 0))
        {
            sb.AppendLine($"  {row.Account.Code} {row.Account.Name,-45} {Money(row.Balance),15}");
        }

        foreach (TaxObligation o in sim.Companies.TaxesOf(co).Unpaid.OrderBy(o => o.DueDate))
        {
            sb.AppendLine("  " + TaxText.Line(I18n, o));
        }

        _monitor.Log(sb.ToString().TrimEnd(), LogLevel.Info);
    }

    private void ExportsCmd(EconomySimulation sim, string[] args)
    {
        var sb = new StringBuilder();
        sb.AppendLine(I18n.Get("cmd.exports.title"));
        foreach (CommodityDefinition commodity in sim.Data.Commodities)
        {
            decimal duty = sim.Data.Trade.ExportDuty(commodity.Id, sim.Difficulty.TradeDuties);
            sb.AppendLine(I18n.Get("cmd.exports.duty", new
            {
                name = commodity.Name,
                duty = Formatting.Percent(I18n, (double)duty),
                fas = Formatting.Percent(I18n, (double)(duty * sim.Data.Trade.FasPassThrough)),
            }));
        }

        foreach (SiloItem item in sim.Companies.State.ExportContainer)
        {
            sb.AppendLine($"  {item.ItemId} ×{item.Stack} (calidad {item.Quality})");
        }

        foreach (ZuzuMarket.Core.Trade.ExportReceivable r in sim.Companies.State.Receivables)
        {
            sb.AppendLine(I18n.Get("cmd.exports.receivable", new { amount = Money(r.Amount), date = GameDateBridge.Format(I18n, r.CollectOn) }));
        }

        _monitor.Log(sb.ToString().TrimEnd(), LogLevel.Info);
    }

    private void CompanyEpsCmd(EconomySimulation sim, string[] args)
    {
        if (!_config.DebugMode)
        {
            _monitor.Log(I18n.Get("cmd.debug-only", new { command = "zuzu_company_eps" }), LogLevel.Warn);
            return;
        }

        if (args.Length < 2 || !long.TryParse(args[1], out long amount) || !sim.Companies.DebugProfit(args[0], amount, sim.CurrentDate))
        {
            _monitor.Log(I18n.Get("cmd.company-eps.usage"), LogLevel.Info);
            return;
        }

        _monitor.Log(I18n.Get("cmd.company-eps.done", new { id = args[0], amount = Money(amount) }), LogLevel.Info);
    }

    private void UnlockCompaniesCmd(EconomySimulation sim, string[] args)
    {
        if (!_config.DebugMode)
        {
            _monitor.Log(I18n.Get("cmd.debug-only", new { command = "zuzu_unlock_companies" }), LogLevel.Warn);
            return;
        }

        sim.Companies.State.UnlockAll = true;
        _monitor.Log(I18n.Get("cmd.unlock-companies.done"), LogLevel.Info);
    }

    private void PlacePhone(EconomySimulation sim, string[] args)
    {
        if (Game1.currentLocation?.Name != "Town")
        {
            _monitor.Log(I18n.Get("cmd.place-phone.not-town"), LogLevel.Warn);
            return;
        }

        var tile = Game1.player.GetGrabTile();
        _phone.MoveTo((int)tile.X, (int)tile.Y);
        _helper.WriteConfig(_config);
        _monitor.Log(I18n.Get("cmd.place-phone.done", new { x = (int)tile.X, y = (int)tile.Y }), LogLevel.Info);
    }

    /// <summary>Descripción del asiento en el idioma actual (si tiene clave de traducción).</summary>
    private string Describe(JournalEntry entry)
    {
        if (entry.DescriptionKey is null)
        {
            return entry.Description;
        }

        var tokens = new Dictionary<string, string>();
        foreach (var (key, value) in entry.DescriptionArgs ?? new Dictionary<string, string>())
        {
            tokens[key] = key switch
            {
                "tax" when Enum.TryParse(value, out TaxKind kind) => TaxText.Tax(I18n, kind),
                "season" when Enum.TryParse(value, out Season season) => GameDateBridge.SeasonName(I18n, season),
                "activity" => I18n.Get($"activity.{value}").HasValue() ? I18n.Get($"activity.{value}") : value,
                _ => value,
            };
        }

        Translation text = I18n.Get(entry.DescriptionKey, tokens);
        return text.HasValue() ? text : entry.Description;
    }

    /// <summary>Precio de venta vanilla del primer objeto del commodity (calidad normal, sin profesiones).</summary>
    private static int? ReferencePrice(CommodityDefinition c) =>
        Game1.objectData.TryGetValue(c.ItemIds[0], out var data) ? data.Price : null;

    private string CommodityName(CommodityDefinition c)
    {
        Translation name = I18n.Get($"commodity.{c.Id}");
        return name.HasValue() ? name : c.Name;
    }

    private string Money(decimal amount) => Formatting.Money(I18n, amount);
}
