using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using ZuzuMarket.Core.Companies;

namespace ZuzuMarket.Game.UI;

/// <summary>Mercado de capitales de tu S.A.: emisión de ON, IPO, recompra y colocación (Fase 7B).</summary>
internal sealed partial class TerminalMenu
{
    private static readonly int[] PrimaryOptions = { 0, 10, 20, 30 };
    private static readonly int[] SecondaryOptions = { 0, 10, 20, 30, 40 };
    private static readonly long[] ShareBlocks = { 1_000, 5_000, 10_000, 25_000 };

    private void DrawCapitalMarket(SpriteBatch b, Rectangle area, CompanyState c)
    {
        int y = area.Y;
        Text(b, T("terminal.capital.title", new { name = CompanyName(c), ticker = c.Ticker ?? "-" }), area.X, y);
        y += 40;
        if (!Companies.IsSA(c) || c.Ticker is null)
        {
            foreach (string line in Wrap(T("terminal.capital.not-sa"), area.Width).Take(3))
            {
                Text(b, line, area.X, y, Muted);
                y += 30;
            }

            return;
        }

        y = DrawBondIssue(b, area, c, y);
        y += 6;
        b.Draw(Game1.staminaRect, new Rectangle(area.X, y, area.Width, 2), Muted * 0.5f);
        y += 12;
        if (c.Listing is null)
        {
            DrawIpo(b, area, c, y);
        }
        else
        {
            DrawListed(b, area, c, y);
        }
    }

    private int DrawBondIssue(SpriteBatch b, Rectangle area, CompanyState c, int y)
    {
        CapitalMarketRules rules = _sim.Data.PlayerCompanies.CapitalMarket;
        int x = area.X;
        foreach (int term in rules.BondTerms)
        {
            int t = term;
            Button(b, new Rectangle(x, y, 160, 42), T("terminal.credit.seasons", new { count = t }), () => _bondTerm = t, selected: _bondTerm == t);
            x += 168;
        }

        foreach (int installments in new[] { 1, 4 })
        {
            int n = installments;
            Button(b, new Rectangle(x, y, 120, 42), T(n == 1 ? "terminal.capital.bullet" : "terminal.capital.installments"), () => _bondInstallments = n,
                selected: _bondInstallments == n);
            x += 128;
        }

        y += 50;
        decimal limit = Companies.BondLimit(c);
        DrawAmountControls(b, area.X, y, limit, () => _bondAmount, v => _bondAmount = v, area.Width);
        y += 58;
        CompanyError error = Companies.CanIssueBond(c, _bondAmount, _bondTerm);
        string status = error == CompanyError.None
            ? T("terminal.capital.bond-quote", new { coupon = Formatting.Percent(I18n, Companies.BondCoupon(c, _bondTerm)), limit = Money(limit) })
            : T($"company.error.{error}");
        string sid = c.Id;
        Button(b, new Rectangle(area.Right - 200, y, 200, 50), T("terminal.capital.issue"), () =>
            CompanyResult(_actions.IssueBond(_sim, sid, _bondAmount, _bondTerm, _bondInstallments), T("terminal.capital.issued")),
            enabled: error == CompanyError.None);
        return StatusBeside(b, status, error == CompanyError.None ? Muted : Negative, area.X, y, area.Width - 220);
    }

    private void DrawIpo(SpriteBatch b, Rectangle area, CompanyState c, int y)
    {
        CapitalMarketRules rules = _sim.Data.PlayerCompanies.CapitalMarket;
        decimal price = Companies.IpoPrice(c);
        Text(b, T("terminal.capital.ipo-price", new { price = Money(price), eps = Formatting.Number(I18n, Companies.MarketEps(c), "0.000") }), area.X, y, Muted);
        y += 34;
        Text(b, T("terminal.capital.primary"), area.X, y + 8, Muted);
        int x = area.X + 150;
        foreach (int pct in PrimaryOptions)
        {
            int p = pct;
            Button(b, new Rectangle(x, y, 80, 40), $"{p}%", () => _primaryPct = p, selected: _primaryPct == p);
            x += 86;
        }

        y += 46;
        Text(b, T("terminal.capital.secondary"), area.X, y + 8, Muted);
        x = area.X + 150;
        foreach (int pct in SecondaryOptions)
        {
            int p = pct;
            Button(b, new Rectangle(x, y, 80, 40), $"{p}%", () => _secondaryPct = p, selected: _secondaryPct == p);
            x += 86;
        }

        y += 50;
        long primary = rules.IpoShares * _primaryPct / 100;
        long secondary = rules.IpoShares * _secondaryPct / 100;
        decimal fee = 1 - rules.UnderwritingFee;
        decimal publicShare = (decimal)(primary + secondary) / (rules.IpoShares + primary);
        CompanyError error = Companies.CanIpo(c, primary, secondary);
        string status = error == CompanyError.None
            ? T("terminal.capital.ipo-summary", new
            {
                company = Money(price * primary * fee),
                player = Money(price * secondary * fee),
                pct = Formatting.Percent(I18n, (double)publicShare),
            })
            : T($"company.error.{error}");
        string sid = c.Id;
        Button(b, new Rectangle(area.Right - 200, y, 200, 50), T("terminal.capital.ipo"), () =>
            CompanyResult(_actions.Ipo(_sim, sid, primary, secondary), T("terminal.capital.listed", new { ticker = c.Ticker })),
            enabled: error == CompanyError.None);
        StatusBeside(b, status, error == CompanyError.None ? Muted : Negative, area.X, y, area.Width - 220);
    }

    private void DrawListed(SpriteBatch b, Rectangle area, CompanyState c, int y)
    {
        CapitalMarketRules rules = _sim.Data.PlayerCompanies.CapitalMarket;
        ListingState listing = c.Listing!;
        decimal price = Companies.MarketPrice(c);
        Text(b, T("terminal.capital.listed-info", new
        {
            price = Money(price),
            cap = Money(price * listing.TotalShares),
            pct = Formatting.Percent(I18n, (double)listing.PlayerFraction),
        }), area.X, y, Muted);
        y += 34;
        int x = area.X;
        foreach (long block in ShareBlocks)
        {
            long s = block;
            Button(b, new Rectangle(x, y, 120, 40), Formatting.Number(I18n, s, "#,0"), () => _shareBlock = s, selected: _shareBlock == s);
            x += 128;
        }

        y += 50;
        decimal buyCost = price * (1 + rules.BuybackPremium) * Math.Min(_shareBlock, listing.PublicShares);
        decimal sellNet = price * (1 - rules.FollowOnDiscount) * (1 - rules.UnderwritingFee) * _shareBlock;
        int w = (area.Width - 10) / 2;
        string sid = c.Id;
        long buyShares = Math.Min(_shareBlock, listing.PublicShares);
        Button(b, new Rectangle(area.X, y, w, 50), T("terminal.capital.buyback"), () =>
            CompanyResult(_actions.Buyback(_sim, sid, buyShares), T("terminal.capital.buyback-done")),
            enabled: buyShares > 0 && buyCost <= Game1.player.Money);
        bool canSell = (decimal)(listing.PublicShares + _shareBlock) / listing.TotalShares <= rules.MaxFloat;
        Button(b, new Rectangle(area.X + w + 10, y, w, 50), T("terminal.capital.follow-on"), () =>
            CompanyResult(_actions.FollowOn(_sim, sid, _shareBlock), T("terminal.capital.follow-on-done")),
            enabled: canSell);
        y += 56;
        foreach (string line in Wrap(T("terminal.capital.listed-quote", new { buy = Money(buyCost), shares = Formatting.Number(I18n, buyShares, "#,0"), sell = Money(sellNet) }), area.Width).Take(2))
        {
            Text(b, line, area.X, y, Muted);
            y += 28;
        }
    }
}
