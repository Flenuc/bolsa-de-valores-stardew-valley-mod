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

    public TerminalActions(BrokerMail mail, MoneyTracker money)
    {
        _mail = mail;
        _money = money;
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
