using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using ZuzuMarket.Core.Macro;
using ZuzuMarket.Core.Markets;
using ZuzuMarket.Game.Content;

namespace ZuzuMarket.Game.UI;

/// <summary>Pestaña Dólar: cotizaciones del oro gotoro, brecha, cuenta en GTD, oficial con cupo, MEP y billetes.</summary>
internal sealed partial class TerminalMenu
{
    private long _fxAmount = 100;

    private string Gtd(decimal amount) => T("terminal.fx.gtd", new { amount = amount.ToString("#,0.00", Formatting.Culture(I18n)) });

    private void DrawDollar(SpriteBatch b, Rectangle area)
    {
        int split = area.X + (int)(area.Width * 0.45);
        DrawFxQuotes(b, new Rectangle(area.X, area.Y, split - area.X - 30, area.Height));
        DrawFxOperations(b, new Rectangle(split, area.Y, area.Right - split, area.Height));
    }

    private void DrawFxQuotes(SpriteBatch b, Rectangle area)
    {
        FxDifficultyRules r = _sim.FxRules;
        int y = area.Y;
        Text(b, T("terminal.fx.quotes"), area.X, y);
        y += 40;
        var rows = new List<(string Label, string Value)>
        {
            (T("terminal.fx.official"), $"{Money(Broker.OfficialBid)} / {Money(Broker.OfficialAsk)}"),
        };
        if (r.Cepo)
        {
            rows.Add((T("terminal.fx.mep"), Money(Broker.Mep)));
            rows.Add((T("terminal.fx.blue"), $"{Money(ModObjects.BlueBid(_sim))} / {Money(ModObjects.BlueAsk(_sim))}"));
            rows.Add((T("terminal.fx.gap"), Formatting.Percent(I18n, _sim.Fx.Gap)));
            rows.Add((T("terminal.fx.quota"), Gtd(Broker.QuotaLeft(Date))));
        }
        else
        {
            rows.Add((T("terminal.fx.free"), ""));
        }

        foreach ((string label, string value) in rows)
        {
            Text(b, label, area.X, y, Muted);
            TextRight(b, value, area.Right, y);
            y += 32;
        }

        if (_sim.Fx.State.Devaluations.LastOrDefault() is { } last)
        {
            Text(b, T("terminal.fx.last-devaluation", new { date = ShortDate(last.Date), size = Formatting.Percent(I18n, last.Size) }), area.X, y, Negative);
            y += 32;
        }

        y += 8;
        Text(b, T(r.Cepo ? "terminal.fx.chart-mep" : "terminal.fx.chart"), area.X, y, Muted);
        y += 30;
        List<double> series = _sim.Fx.State.History.Select(h => r.Cepo ? h.Mep : h.Official).ToList();
        if (series.Count >= 2)
        {
            Chart(b, new Rectangle(area.X, y, area.Width, Math.Max(120, area.Bottom - y - 10)), series, label: v => Money((decimal)v));
        }
    }

    private void DrawFxOperations(SpriteBatch b, Rectangle area)
    {
        int y = area.Y;
        Text(b, T("terminal.fx.account", new { gtd = Gtd(Broker.Gtd), value = Money(Broker.GotoroValue) }), area.X, y);
        y += 40;
        Text(b, T("terminal.fx.amount"), area.X, y + 12, Muted);
        DrawAmountControls(b, area.X + 130, y, Math.Max(Broker.Gtd, Broker.Cash / Math.Max(1, Broker.OfficialAsk)), () => _fxAmount, v => _fxAmount = v, area.Width - 130);
        y += 62;

        int w = (area.Width - 10) / 2;
        decimal amount = _fxAmount;
        Button(b, new Rectangle(area.X, y, w, 50), T("terminal.fx.buy-official", new { cost = Money(amount * Broker.OfficialAsk) }), () =>
            Result(Broker.BuyOfficial(amount, Date, Time), T("terminal.fx.bought")));
        Button(b, new Rectangle(area.X + w + 10, y, w, 50), T("terminal.fx.sell-official", new { proceeds = Money(amount * Broker.OfficialBid) }), () =>
            Result(Broker.SellOfficial(amount, Date, Time), T("terminal.fx.sold")), enabled: Broker.Gtd >= amount);
        y += 58;

        Button(b, new Rectangle(area.X, y, w, 50), T("terminal.fx.buy-mep", new { cost = Money(amount * Broker.Mep) }), () =>
            Result(Broker.BuyMep(amount * Broker.Mep, Date, Time), T("terminal.fx.mep-started", new { date = ShortDate(Broker.MepSettlement(Date)) })),
            enabled: Broker.MepBond is not null);
        Button(b, new Rectangle(area.X + w + 10, y, w, 50), T("terminal.fx.sell-mep"), () =>
            Result(Broker.SellMep(amount, Date, Time), T("terminal.fx.mep-started", new { date = ShortDate(Broker.MepSettlement(Date)) })),
            enabled: Broker.MepBond is not null && Broker.Gtd >= amount);
        y += 58;

        foreach (string line in Wrap(T(_sim.FxRules.Cepo ? "terminal.fx.help-cepo" : "terminal.fx.help-free"), area.Width).Take(4))
        {
            Text(b, line, area.X, y, Muted);
            y += 28;
        }

        y += 6;
        int notes = TerminalActions.NotesInInventory();
        Button(b, new Rectangle(area.X, y, area.Width, 50), T("terminal.fx.deposit-notes", new { count = notes }), () =>
            Result(_actions.DepositNotes(_sim), T("terminal.fx.deposited")), enabled: notes > 0);
        y += 60;

        foreach (MepOrder order in Broker.State.MepOrders)
        {
            foreach (string line in Wrap(T(order.ToGotoro ? "terminal.fx.pending-to-gtd" : "terminal.fx.pending-to-local", new { bond = order.BondId, units = order.Units, date = ShortDate(order.SettleOn) }), area.Width).Take(2))
            {
                Text(b, line, area.X, y, Muted);
                y += 28;
            }
        }
    }
}
