using StardewValley;
using ZuzuMarket.Core.Markets;
using ZuzuMarket.Core.Simulation;
using ZuzuMarket.Game.Bridges;
using ZuzuMarket.Game.Content;

namespace ZuzuMarket.Game.UI;

/// <summary>Acciones de la Terminal que tocan el oro del jugador (el resto opera solo dentro de la cuenta comitente).</summary>
internal sealed class TerminalActions
{
    private readonly BrokerMail _mail;
    private readonly MoneyTracker _money;
    private readonly SiloBridge _silo;

    public TerminalActions(BrokerMail mail, MoneyTracker money, SiloBridge silo)
    {
        _mail = mail;
        _money = money;
        _silo = silo;
    }

    public void OpenSilo(EconomySimulation sim) => _silo.Open(sim);

    /// <summary>Pide un préstamo: el oro entra al bolsillo.</summary>
    public TradeError TakeLoan(EconomySimulation sim, long amount, int term)
    {
        var notices = new List<ZuzuMarket.Core.Legal.LegalNotice>();
        ZuzuMarket.Core.Legal.LoanQuote q = sim.Legal.TakeLoan(amount, term, sim.CurrentDate, notices);
        if (q.Error == TradeError.None)
        {
            Game1.player.Money += (int)q.Amount;
            _money.Reset();
        }

        return q.Error;
    }

    public void OpenAccount(EconomySimulation sim)
    {
        if (!sim.Broker.IsOpen)
        {
            sim.Broker.Open(sim.CurrentDate);
            _mail.SendWelcome();
        }
    }

    public TradeError Deposit(EconomySimulation sim, long amount)
    {
        if (amount <= 0)
        {
            return TradeError.InvalidQuantity;
        }

        if (Game1.player.Money < amount)
        {
            return TradeError.InsufficientFunds;
        }

        TradeError error = sim.Broker.Deposit(amount, sim.CurrentDate);
        if (error == TradeError.None)
        {
            Game1.player.Money -= (int)amount;
            _money.Reset();
        }

        return error;
    }

    public TradeError Withdraw(EconomySimulation sim, long amount)
    {
        TradeError error = sim.Broker.Withdraw(amount, sim.CurrentDate);
        if (error == TradeError.None)
        {
            Game1.player.Money += (int)amount;
            _money.Reset();
        }

        return error;
    }
}
