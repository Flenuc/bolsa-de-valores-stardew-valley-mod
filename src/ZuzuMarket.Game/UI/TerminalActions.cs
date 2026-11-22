using StardewValley;
using ZuzuMarket.Core.Companies;
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
    private readonly ExportBridge _exports;
    private readonly CompanyMail _companyMail;

    public TerminalActions(BrokerMail mail, MoneyTracker money, SiloBridge silo, ExportBridge exports, CompanyMail companyMail)
    {
        _mail = mail;
        _money = money;
        _silo = silo;
        _exports = exports;
        _companyMail = companyMail;
    }

    public void OpenSilo(EconomySimulation sim) => _silo.Open(sim);

    public void OpenExports(EconomySimulation sim) => _exports.Open(sim);

    public CompanyError CreateCompany(EconomySimulation sim, string type, string form, long capital)
    {
        var notices = new List<CompanyNotice>();
        bool unlocked = sim.Data.PlayerCompanies.FindType(type) is { } t && CompanyUnlocks.IsUnlocked(t);
        CompanyResult r = sim.Companies.Create(type, form, capital, unlocked, Game1.player.Money, sim.CurrentDate, notices);
        return Apply(r, notices);
    }

    public CompanyError Contribute(EconomySimulation sim, string id, long amount) =>
        Apply(sim.Companies.Contribute(id, amount, Game1.player.Money, sim.CurrentDate), new List<CompanyNotice>());

    public CompanyError PayDividend(EconomySimulation sim, string id, long amount) =>
        Apply(sim.Companies.PayDividend(id, amount, sim.CurrentDate), new List<CompanyNotice>());

    public CompanyError Transform(EconomySimulation sim, string id, string form)
    {
        var notices = new List<CompanyNotice>();
        return Apply(sim.Companies.Transform(id, form, Game1.player.Money, sim.CurrentDate, notices), notices);
    }

    public CompanyError Dissolve(EconomySimulation sim, string id)
    {
        var notices = new List<CompanyNotice>();
        return Apply(sim.Companies.Dissolve(id, sim.CurrentDate, notices), notices);
    }

    public CompanyError IssueBond(EconomySimulation sim, string id, long amount, int term, int installments)
    {
        var notices = new List<CompanyNotice>();
        return Apply(sim.Companies.IssueBond(id, amount, term, installments, sim.CurrentDate, notices), notices);
    }

    public CompanyError Ipo(EconomySimulation sim, string id, long primary, long secondary)
    {
        var notices = new List<CompanyNotice>();
        return Apply(sim.Companies.Ipo(id, primary, secondary, sim.CurrentDate, notices), notices);
    }

    public CompanyError Buyback(EconomySimulation sim, string id, long shares)
    {
        var notices = new List<CompanyNotice>();
        return Apply(sim.Companies.Buyback(id, shares, Game1.player.Money, sim.CurrentDate, notices), notices);
    }

    public CompanyError FollowOn(EconomySimulation sim, string id, long shares)
    {
        var notices = new List<CompanyNotice>();
        return Apply(sim.Companies.FollowOn(id, shares, sim.CurrentDate, notices), notices);
    }

    private CompanyError Apply(CompanyResult r, List<CompanyNotice> notices)
    {
        if (r.Error == CompanyError.None)
        {
            Game1.player.Money = Math.Max(0, Game1.player.Money + (int)Math.Floor(r.GoldDelta));
            _money.Reset();
            _companyMail.Publish(notices);
        }

        return r.Error;
    }

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
