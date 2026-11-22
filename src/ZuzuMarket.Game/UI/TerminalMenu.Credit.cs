using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using ZuzuMarket.Core.Legal;
using ZuzuMarket.Core.Markets;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Game.UI;

/// <summary>Pestaña Crédito: calificación, préstamos del Banco de Zuzu, caución tomadora y situación legal.</summary>
internal sealed partial class TerminalMenu
{
    private long _loanAmount = 1_000;
    private int _loanTerm = 4;
    private long _repoAmount = 500;
    private int _repoDays = 7;

    private LegalEngine Legal => _sim.Legal;

    private void DrawCredit(SpriteBatch b, Rectangle area)
    {
        int split = area.X + (int)(area.Width * 0.42);
        DrawCreditStatus(b, new Rectangle(area.X, area.Y, split - area.X - 30, area.Height));
        DrawLoanForm(b, new Rectangle(split, area.Y, area.Right - split, area.Height));
    }

    private void DrawCreditStatus(SpriteBatch b, Rectangle area)
    {
        int y = area.Y;
        Text(b, T("terminal.credit.rating", new { rating = Legal.Rating, score = Formatting.Number(I18n, Legal.State.Score, "0") }), area.X, y, Game1.textColor, Game1.dialogueFont);
        y += 56;

        var rows = new (string Label, string Value)[]
        {
            (T("terminal.credit.net-worth"), Money(Legal.NetWorth)),
            (T("terminal.credit.revenue"), Money(Legal.AnnualRevenue)),
            (T("terminal.credit.limit"), Money(Legal.LoanLimit)),
            (T("terminal.credit.rate"), Formatting.Percent(I18n, Legal.LoanRate)),
            (T("terminal.credit.debt"), Money(Legal.DebtInCollection)),
        };
        foreach ((string label, string value) in rows)
        {
            Text(b, label, area.X, y, Muted);
            TextRight(b, value, area.Right, y);
            y += 32;
        }

        y += 14;
        Text(b, T("terminal.credit.legal"), area.X, y, Muted);
        y += 34;
        foreach (string status in LegalStatus())
        {
            foreach (string line in Wrap(status, area.Width).Take(2))
            {
                Text(b, line, area.X, y, Legal.IsInhibited ? Negative : Positive);
                y += 30;
            }
        }

        y += 14;
        TradeError canConcurso = Legal.CanRequestConcurso(Date);
        Button(b, new Rectangle(area.X, y, Math.Min(area.Width, 360), 56), T("terminal.credit.concurso"), () =>
        {
            LegalNotice? notice = _sim.RequestConcurso();
            if (notice is not null)
            {
                Say(T("terminal.credit.concurso-done", new { amount = Money(notice.Amount) }), Positive);
            }
        }, enabled: canConcurso == TradeError.None);
        y += 66;
        foreach (string line in Wrap(T("terminal.credit.concurso-help"), area.Width).Take(Math.Max(0, (area.Bottom - y) / 28)))
        {
            Text(b, line, area.X, y, Muted);
            y += 28;
        }
    }

    private IEnumerable<string> LegalStatus()
    {
        bool any = false;
        if (Legal.State.EmbargoSince is EconomicDate embargo)
        {
            any = true;
            yield return T("terminal.credit.embargo", new { date = ShortDate(embargo) });
        }

        if (Legal.State.ConcursoActive)
        {
            any = true;
            yield return T("terminal.credit.concurso-active");
        }

        if (Legal.State.InhabilitadoUntil is EconomicDate until)
        {
            any = true;
            yield return T("terminal.credit.inhabilitado", new { date = ShortDate(until) });
        }

        if (!any)
        {
            yield return T("terminal.credit.clean");
        }
    }

    /// <summary>Texto ajustado al ancho libre a la izquierda de un botón; devuelve la Y debajo de ambos.</summary>
    private int StatusBeside(SpriteBatch b, string text, Color color, int x, int y, int width)
    {
        int lineY = y;
        foreach (string line in Wrap(text, width).Take(3))
        {
            Text(b, line, x, lineY, color);
            lineY += 28;
        }

        return Math.Max(lineY, y + 52) + 10;
    }

    private void DrawLoanForm(SpriteBatch b, Rectangle area)
    {
        int y = area.Y;
        Text(b, T("terminal.credit.loan-title"), area.X, y);
        y += 40;
        Text(b, T("terminal.credit.term"), area.X, y + 10, Muted);
        int x = area.X + 100;
        foreach (int term in _sim.Data.Legal.LoanTerms)
        {
            int t = term;
            Button(b, new Rectangle(x, y, 170, 46), T("terminal.credit.seasons", new { count = t }), () => _loanTerm = t, selected: _loanTerm == t);
            x += 178;
        }

        y += 58;
        DrawAmountControls(b, area.X, y, Legal.LoanLimit, () => _loanAmount, v => _loanAmount = v);
        y += 62;
        LoanQuote quote = Legal.QuoteLoan(_loanAmount, _loanTerm, Date);
        string loanText = quote.Error == TradeError.None
            ? T("terminal.credit.loan-quote", new { installment = Money(quote.Installment), total = Money(quote.Installment * (long)_loanTerm) })
            : T($"error.{quote.Error}");
        Button(b, new Rectangle(area.Right - 230, y, 230, 52), T("terminal.credit.take-loan"), () =>
        {
            TradeError error = _actions.TakeLoan(_sim, _loanAmount, _loanTerm);
            Result(error, T("terminal.credit.loan-done", new { amount = Money(_loanAmount) }));
        }, enabled: quote.Error == TradeError.None);
        y = StatusBeside(b, loanText, quote.Error == TradeError.None ? Muted : Negative, area.X, y, area.Width - 250);

        foreach (Loan loan in Legal.State.Loans)
        {
            Text(b, T("terminal.credit.loan-active", new
            {
                id = loan.Id,
                outstanding = Money(loan.Outstanding),
                installment = Money(loan.Installment),
                paid = loan.InstallmentsIssued,
                term = loan.TermSeasons,
            }), area.X, y);
            y += 30;
        }

        // Caución tomadora.
        y += 10;
        b.Draw(Game1.staminaRect, new Rectangle(area.X, y, area.Width, 2), Muted * 0.5f);
        y += 14;
        Text(b, T("terminal.credit.repo-title", new
        {
            rate = Formatting.Percent(I18n, Broker.RepoRate),
            limit = Money(Broker.RepoLimit(Date, Time)),
        }), area.X, y);
        y += 40;
        x = area.X;
        foreach (int days in _sim.Data.Legal.RepoTerms)
        {
            int d = days;
            Button(b, new Rectangle(x, y, 110, 46), T("terminal.caucion.days", new { days = d }), () => _repoDays = d, selected: _repoDays == d);
            x += 118;
        }

        y += 58;
        DrawAmountControls(b, area.X, y, Broker.RepoLimit(Date, Time), () => _repoAmount, v => _repoAmount = v);
        y += 62;
        TradeError repoError = Broker.ValidateRepo(_repoAmount, _repoDays, Date, Time);
        (EconomicDate maturity, decimal interest) = Broker.RepoTerms(_repoAmount, _repoDays, Date);
        string repoText = repoError == TradeError.None
            ? T("terminal.credit.repo-quote", new { date = ShortDate(maturity), total = Money(_repoAmount + interest) })
            : T($"error.{repoError}");
        Button(b, new Rectangle(area.Right - 230, y, 230, 52), T("terminal.credit.take-repo"), () =>
        {
            Result(Broker.BorrowRepo(_repoAmount, _repoDays, Date, Time), T("terminal.credit.repo-done"));
        }, enabled: repoError == TradeError.None);
        y = StatusBeside(b, repoText, repoError == TradeError.None ? Muted : Negative, area.X, y, area.Width - 250);
        foreach (Caucion repo in Broker.State.Repos)
        {
            Text(b, T("terminal.credit.repo-active", new { amount = Money(repo.Principal), date = ShortDate(repo.Maturity), interest = Money(repo.Interest) }), area.X, y);
            y += 30;
        }
    }
}
