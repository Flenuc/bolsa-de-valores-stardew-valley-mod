using System.Globalization;
using System.Text;
using StardewModdingAPI;
using ZuzuMarket.Core.Ledger;
using ZuzuMarket.Core.Random;
using ZuzuMarket.Core.Simulation;
using ZuzuMarket.Core.Time;
using ZuzuMarket.Game.Bridges;

namespace ZuzuMarket.Game.Commands;

/// <summary>Comandos de consola de SMAPI para inspeccionar y depurar la simulación.</summary>
internal sealed class ConsoleCommands
{
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly ModConfig _config;
    private readonly Func<EconomySimulation?> _getSimulation;
    private readonly Action _reset;

    public ConsoleCommands(IModHelper helper, IMonitor monitor, ModConfig config, Func<EconomySimulation?> getSimulation, Action reset)
    {
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
            sb.AppendLine($"#{entry.Id} · {GameDateBridge.Format(I18n, entry.Date)} · {entry.EntityId} · [{entry.Source}] {entry.Description}");
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
            _monitor.Log(I18n.Get("cmd.reset.debug-only"), LogLevel.Warn);
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

    private static string Money(decimal amount) => amount.ToString("#,0.00", CultureInfo.InvariantCulture) + "g";
}
