using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using ZuzuMarket.Core.Simulation;
using ZuzuMarket.Core.Taxation;
using ZuzuMarket.Game.Bridges;

namespace ZuzuMarket.Game.Content;

/// <summary>
/// Pago de boletas en la Municipalidad: acción sobre el escritorio de Lewis (ubicación en <c>taxes.json</c>,
/// <c>paymentDesk</c>), y también el débito automático de cada mañana.
/// </summary>
internal sealed class Municipalidad
{
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly Func<EconomySimulation?> _getSimulation;
    private readonly TaxMail _mail;
    private readonly MoneyTracker _money;

    public Municipalidad(IModHelper helper, IMonitor monitor, Func<EconomySimulation?> getSimulation, TaxMail mail, MoneyTracker money)
    {
        _helper = helper;
        _monitor = monitor;
        _getSimulation = getSimulation;
        _mail = mail;
        _money = money;
    }

    private ITranslationHelper I18n => _helper.Translation;

    public void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        EconomySimulation? sim = _getSimulation();
        if (sim is null || !Context.IsPlayerFree || !e.Button.IsActionButton() || Game1.currentLocation is null)
        {
            return;
        }

        PaymentDesk desk = sim.Data.Taxes.PaymentDesk;
        if (!desk.Contains(Game1.currentLocation.NameOrUniqueName, (int)e.Cursor.GrabTile.X, (int)e.Cursor.GrabTile.Y))
        {
            return;
        }

        // Sin boletas pendientes, el libro de finanzas del pueblo responde como siempre.
        if (!sim.Taxes.Unpaid.Any())
        {
            return;
        }

        _helper.Input.Suppress(e.Button);
        OpenDesk(sim);
    }

    public void OpenDesk(EconomySimulation sim)
    {
        List<TaxObligation> unpaid = sim.Taxes.Unpaid.OrderBy(o => o.DueDate).ThenBy(o => o.Id).ToList();
        if (unpaid.Count == 0)
        {
            Game1.drawObjectDialogue(I18n.Get("desk.nothing"));
            return;
        }

        long total = unpaid.Sum(o => o.Total);
        TaxObligation oldest = unpaid[0];
        var responses = new[]
        {
            new Response("all", I18n.Get("desk.pay-all", new { total = Formatting.Gold(I18n, total) })),
            new Response("oldest", I18n.Get("desk.pay-oldest", new { line = TaxText.Line(I18n, oldest) })),
            new Response("cancel", I18n.Get("desk.cancel")),
        };

        Game1.currentLocation.createQuestionDialogue(
            I18n.Get("desk.question", new { count = unpaid.Count, total = Formatting.Gold(I18n, total) }),
            responses,
            (_, answer) =>
            {
                if (answer == "all")
                {
                    Pay(sim, unpaid, automatic: false);
                }
                else if (answer == "oldest")
                {
                    Pay(sim, new[] { oldest }, automatic: false);
                }
            });
    }

    /// <summary>Débito automático: paga lo vencido o que vence hoy, mientras alcance el oro.</summary>
    public void AutoDebit(EconomySimulation sim) => Pay(sim, sim.Taxes.DueOn(sim.CurrentDate), automatic: true);

    /// <summary>Paga en orden; se detiene en la primera boleta que no alcanza a cubrir.</summary>
    public void Pay(EconomySimulation sim, IEnumerable<TaxObligation> obligations, bool automatic)
    {
        long paidTotal = 0;
        int paidCount = 0;
        bool short_ = false;
        foreach (TaxObligation o in obligations.ToList())
        {
            if (o.IsPaid)
            {
                continue;
            }

            if (Game1.player.Money < o.Total)
            {
                short_ = true;
                break;
            }

            long? paid = sim.PayObligation(o.Id);
            if (paid is long amount)
            {
                Game1.player.Money -= (int)amount;
                paidTotal += amount;
                paidCount++;
                _monitor.Log($"Pagada la boleta {o.Id} ({o.Kind} {o.Period}): {amount}g.", LogLevel.Debug);
            }
        }

        _money.Reset();
        if (paidCount > 0)
        {
            string key = automatic ? "hud.autodebit" : "hud.paid";
            _mail.Hud(I18n.Get(key, new { count = paidCount, total = Formatting.Gold(I18n, paidTotal) }));
        }

        if (short_)
        {
            _mail.Hud(I18n.Get(automatic ? "hud.autodebit-short" : "hud.paid-short"), 3);
        }
    }
}
