using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using ZuzuMarket.Core.Events;
using ZuzuMarket.Core.Markets;
using ZuzuMarket.Core.Simulation;
using ZuzuMarket.Core.Time;
using ZuzuMarket.Game.Bridges;

namespace ZuzuMarket.Game.UI;

/// <summary>
/// Terminal Bursátil de Valle Inversiones: panel, acciones, renta fija, cartera y cuenta.
/// Se dibuja en "modo inmediato": cada cuadro registra las zonas clickeables que dibuja.
/// </summary>
internal sealed class TerminalMenu : IClickableMenu
{
    private enum Tab
    {
        Panel,
        Stocks,
        FixedIncome,
        Portfolio,
        Account,
    }

    private sealed class Ticket
    {
        public string Instrument { get; set; } = "";

        public TradeSide Side { get; set; }

        public long Quantity { get; set; } = 1;
    }

    private static readonly Color Positive = new(0, 120, 40);
    private static readonly Color Negative = new(170, 30, 30);
    private static readonly Color Muted = new(110, 90, 70);

    private readonly IModHelper _helper;
    private readonly EconomySimulation _sim;
    private readonly TerminalActions _actions;
    private readonly List<(Rectangle Bounds, Action Action)> _hotspots = new();
    private int _overlayStart = -1;

    private Tab _tab = Tab.Panel;
    private string _selectedStock = "";
    private Ticket? _ticket;
    private long _amount = 100;
    private int _caucionDays = 7;
    private string? _message;
    private Color _messageColor;

    public TerminalMenu(IModHelper helper, EconomySimulation sim, TerminalActions actions)
    {
        _helper = helper;
        _sim = sim;
        _actions = actions;
        _selectedStock = sim.Stocks.Companies.FirstOrDefault()?.Ticker ?? "";
        Layout();
    }

    private ITranslationHelper I18n => _helper.Translation;

    private EconomicDate Date => _sim.CurrentDate;

    private static int Time => Game1.timeOfDay;

    private Broker Broker => _sim.Broker;

    private string T(string key, object? tokens = null) => I18n.Get(key, tokens);

    // ───────────────────────────── Ciclo de vida ─────────────────────────────

    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
    {
        base.gameWindowSizeChanged(oldBounds, newBounds);
        Layout();
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        base.receiveLeftClick(x, y, playSound);
        if (upperRightCloseButton?.containsPoint(x, y) == true)
        {
            return;
        }

        int start = _overlayStart >= 0 ? _overlayStart : 0;
        for (int i = _hotspots.Count - 1; i >= start; i--)
        {
            if (_hotspots[i].Bounds.Contains(x, y))
            {
                Game1.playSound("smallSelect");
                _hotspots[i].Action();
                return;
            }
        }
    }

    public override void receiveKeyPress(Keys key)
    {
        if (_ticket is not null && (key == Keys.Escape || Game1.options.doesInputListContain(Game1.options.menuButton, key)))
        {
            _ticket = null;
            return;
        }

        base.receiveKeyPress(key);
    }

    public override void draw(SpriteBatch b)
    {
        _hotspots.Clear();
        _overlayStart = -1;
        b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.6f);
        drawTextureBox(b, xPositionOnScreen, yPositionOnScreen, width, height, Color.White);

        int left = xPositionOnScreen + 40;
        int top = yPositionOnScreen + 36;
        int right = xPositionOnScreen + width - 40;

        DrawHeader(b, left, top, right);
        int tabsY = top + 52;
        DrawTabs(b, left, tabsY);
        var content = new Rectangle(left, tabsY + 64, right - left, yPositionOnScreen + height - 60 - (tabsY + 64));

        if (!Broker.IsOpen)
        {
            DrawOpenAccount(b, content);
        }
        else
        {
            switch (_tab)
            {
                case Tab.Panel:
                    DrawPanel(b, content);
                    break;
                case Tab.Stocks:
                    DrawStocks(b, content);
                    break;
                case Tab.FixedIncome:
                    DrawFixedIncome(b, content);
                    break;
                case Tab.Portfolio:
                    DrawPortfolio(b, content);
                    break;
                case Tab.Account:
                    DrawAccount(b, content);
                    break;
            }
        }

        if (_message is not null)
        {
            Text(b, _message, left, yPositionOnScreen + height - 52, _messageColor);
        }

        if (_ticket is not null)
        {
            DrawTicket(b);
        }

        base.draw(b);
        drawMouse(b);
    }

    private void Layout()
    {
        int w = Math.Min(1300, Game1.uiViewport.Width - 64);
        int h = Math.Min(780, Game1.uiViewport.Height - 64);
        initialize((Game1.uiViewport.Width - w) / 2, (Game1.uiViewport.Height - h) / 2, w, h, showUpperRightCloseButton: true);
    }

    // ───────────────────────────── Secciones ─────────────────────────────

    private void DrawHeader(SpriteBatch b, int left, int top, int right)
    {
        Text(b, T("terminal.title"), left, top, Game1.textColor, Game1.dialogueFont);
        bool open = Broker.IsMarketOpen(Date, Time);
        string status = open ? T("terminal.market-open") : T("terminal.market-closed");
        string when = $"{GameDateBridge.Format(I18n, Date)} · {Game1.getTimeOfDayString(Time)} · {status}";
        TextRight(b, when, right - 40, top + 12, open ? Positive : Negative);
    }

    private void DrawTabs(SpriteBatch b, int left, int y)
    {
        int x = left;
        foreach (Tab tab in Enum.GetValues<Tab>())
        {
            string label = T($"terminal.tab.{tab}");
            int w = (int)Game1.smallFont.MeasureString(label).X + 40;
            Button(b, new Rectangle(x, y, w, 52), label, () =>
            {
                _tab = tab;
                _message = null;
            }, enabled: Broker.IsOpen || tab == Tab.Panel, selected: _tab == tab);
            x += w + 8;
        }
    }

    private void DrawOpenAccount(SpriteBatch b, Rectangle area)
    {
        int y = area.Y + 20;
        foreach (string line in Wrap(T("terminal.open.text"), area.Width - 40))
        {
            Text(b, line, area.X + 20, y);
            y += 36;
        }

        Button(b, new Rectangle(area.X + 20, y + 20, 320, 64), T("terminal.open.button"), () =>
        {
            _actions.OpenAccount(_sim);
            _tab = Tab.Account;
            Say(T("terminal.open.done"), Positive);
        });
    }

    private void DrawPanel(SpriteBatch b, Rectangle area)
    {
        int col2 = area.X + (area.Width / 2) + 20;
        int y = area.Y;
        double merzu = _sim.Stocks.MerzuAt(Time);
        double previous = _sim.Stocks.State.MerzuPrevious;
        double change = previous > 0 ? (merzu / previous) - 1 : 0;
        Text(b, "MERZU", area.X, y, Muted);
        Text(b, Formatting.Number(I18n, merzu, "#,0.00"), area.X + 120, y - 8, Game1.textColor, Game1.dialogueFont);
        Text(b, Formatting.Percent(I18n, change, signed: true), area.X + 340, y, change >= 0 ? Positive : Negative);
        y += 60;

        var rows = new List<(string Label, string Value)>
        {
            (T("terminal.panel.reference"), RateText(Broker.ReferenceRate)),
            (T("terminal.panel.caucion"), RateText(Broker.CaucionRate)),
            (T("terminal.panel.inflation"), Formatting.Percent(I18n, _sim.Inflation.PeriodRate)),
            (T("terminal.panel.ipc"), Formatting.Number(I18n, _sim.Inflation.Index)),
            ("", ""),
            (T("terminal.panel.cash"), Money(Broker.Cash)),
            (T("terminal.panel.portfolio"), Money(Broker.PortfolioValue(Date, Time))),
            (T("terminal.panel.total"), Money(Broker.Cash + Broker.PortfolioValue(Date, Time))),
        };
        foreach ((string label, string value) in rows)
        {
            if (label.Length > 0)
            {
                Text(b, label, area.X, y, Muted);
                TextRight(b, value, col2 - 60, y);
            }

            y += 38;
        }

        var history = _sim.Stocks.State.MerzuHistory;
        Text(b, T("terminal.panel.chart"), col2, area.Y, Muted);
        Chart(b, new Rectangle(col2, area.Y + 40, area.Right - col2, 200), history.Skip(Math.Max(0, history.Count - 28)).Select(h => h.Value).ToList(), isIndex: true);

        int ny = area.Y + 270;
        Text(b, T("terminal.panel.news"), col2, ny, Muted);
        ny += 36;
        if (_sim.Events.State.News.Count == 0)
        {
            Text(b, T("terminal.panel.no-news"), col2, ny);
        }

        foreach (NewsItem n in _sim.Events.State.News.Skip(Math.Max(0, _sim.Events.State.News.Count - 5)).Reverse())
        {
            string text = n.Kind == NewsKind.Event
                ? T($"event.{n.Key}.headline")
                : T("news.inflation.short", new { rate = Formatting.Percent(I18n, n.Value) });
            foreach (string line in Wrap(text, area.Right - col2).Take(2))
            {
                Text(b, line, col2, ny);
                ny += 30;
            }

            ny += 6;
        }
    }

    private void DrawStocks(SpriteBatch b, Rectangle area)
    {
        int tableWidth = (int)(area.Width * 0.58);
        int[] cols = { 0, 110, tableWidth - 370, tableWidth - 240, tableWidth - 130 };
        int y = area.Y;
        Text(b, T("terminal.col.ticker"), area.X + cols[0], y, Muted);
        Text(b, T("terminal.col.company"), area.X + cols[1], y, Muted);
        TextRight(b, T("terminal.col.price"), area.X + cols[3] - 20, y, Muted);
        TextRight(b, T("terminal.col.change"), area.X + cols[4] - 10, y, Muted);
        TextRight(b, T("terminal.col.holding"), area.X + tableWidth, y, Muted);
        y += 40;

        foreach (CompanyDefinition c in _sim.Stocks.Companies)
        {
            StockState s = _sim.Stocks.Get(c.Ticker);
            double price = _sim.Stocks.PriceAt(c.Ticker, Time);
            double change = s.PreviousClose > 0 ? (price / s.PreviousClose) - 1 : 0;
            var row = new Rectangle(area.X - 8, y - 4, tableWidth + 16, 40);
            if (c.Ticker == _selectedStock)
            {
                b.Draw(Game1.staminaRect, row, Color.Wheat * 0.6f);
            }

            Hotspot(row, () => _selectedStock = c.Ticker);
            Text(b, c.Ticker, area.X + cols[0], y);
            Text(b, Ellipsis(c.Name, cols[2] - cols[1] - 10), area.X + cols[1], y);
            TextRight(b, Price(price), area.X + cols[3] - 20, y);
            TextRight(b, Formatting.Percent(I18n, change, signed: true), area.X + cols[4] - 10, y, change >= 0 ? Positive : Negative);
            long held = Broker.State.Stocks.TryGetValue(c.Ticker, out Holding? h) ? h.Quantity : 0;
            TextRight(b, held > 0 ? held.ToString("#,0", Formatting.Culture(I18n)) : "-", area.X + tableWidth, y);
            y += 42;
        }

        // Detalle de la empresa elegida.
        CompanyDefinition? selected = _sim.Stocks.Find(_selectedStock);
        if (selected is null)
        {
            return;
        }

        int dx = area.X + tableWidth + 40;
        int dw = area.Right - dx;
        StockState st = _sim.Stocks.Get(selected.Ticker);
        int dy = area.Y;
        Text(b, $"{selected.Ticker} · {Ellipsis(selected.Name, dw - 100)}", dx, dy);
        dy += 34;
        Text(b, T("terminal.stock.sector", new { sector = selected.Sector }), dx, dy, Muted);
        dy += 34;
        double eps = st.Eps;
        double now = _sim.Stocks.PriceAt(selected.Ticker, Time);
        Text(b, T("terminal.stock.metrics", new
        {
            pe = Formatting.Number(I18n, eps > 0 ? now / eps : 0, "0.0"),
            dividend = Price(_sim.Stocks.QuarterlyDividend(selected)),
        }), dx, dy, Muted);
        dy += 40;
        Chart(b, new Rectangle(dx, dy, dw, 190), st.History.Skip(Math.Max(0, st.History.Count - 28)).Select(h => h.Close).ToList());
        dy += 210;
        Text(b, T("terminal.stock.intraday"), dx, dy, Muted);
        dy += 30;
        int tick = Math.Max(0, Math.Min(st.Intraday.Count, ((Time / 100 * 60) + (Time % 100) - 540) / 10));
        Chart(b, new Rectangle(dx, dy, dw, 90), new[] { st.PreviousClose }.Concat(st.Intraday.Take(tick)).ToList());
        dy += 110;
        int bw = (dw - 16) / 2;
        Button(b, new Rectangle(dx, dy, bw, 60), T("terminal.buy"), () => OpenTicket(selected.Ticker, TradeSide.Buy));
        Button(b, new Rectangle(dx + bw + 16, dy, bw, 60), T("terminal.sell"), () => OpenTicket(selected.Ticker, TradeSide.Sell),
            enabled: Broker.State.Stocks.ContainsKey(selected.Ticker));
    }

    private void DrawFixedIncome(SpriteBatch b, Rectangle area)
    {
        int y = area.Y;
        Text(b, T("terminal.letras.title"), area.X, y);
        y += 40;
        int tableWidth = area.Width - 280;
        int[] cols = { 0, (int)(tableWidth * 0.22), (int)(tableWidth * 0.46), (int)(tableWidth * 0.58), (int)(tableWidth * 0.74), (int)(tableWidth * 0.90) };
        string[] headers = { "terminal.col.series", "terminal.col.maturity", "terminal.col.days", "terminal.col.price", "terminal.col.rate-monthly", "terminal.col.holding" };
        for (int i = 0; i < cols.Length; i++)
        {
            Text(b, T(headers[i]), area.X + cols[i], y, Muted);
        }

        y += 36;
        foreach (LetraSeries l in Broker.ActiveLetras(Date))
        {
            long held = Broker.State.Letras.TryGetValue(l.Id, out Holding? h) ? h.Quantity : 0;
            Text(b, l.Id, area.X + cols[0], y);
            Text(b, ShortDate(l.Maturity), area.X + cols[1], y);
            Text(b, Date.DaysUntil(l.Maturity).ToString(), area.X + cols[2], y);
            Text(b, Price(Broker.LetraPrice(l, Date)), area.X + cols[3], y);
            Text(b, Formatting.Percent(I18n, Broker.LetraRate(l)), area.X + cols[4], y);
            Text(b, held > 0 ? held.ToString() : "-", area.X + cols[5], y);
            Button(b, new Rectangle(area.Right - 260, y - 8, 120, 44), T("terminal.buy"), () => OpenTicket(l.Id, TradeSide.Buy));
            Button(b, new Rectangle(area.Right - 130, y - 8, 120, 44), T("terminal.sell"), () => OpenTicket(l.Id, TradeSide.Sell), enabled: held > 0);
            y += 50;
        }

        y += 20;
        b.Draw(Game1.staminaRect, new Rectangle(area.X, y, area.Width, 2), Muted * 0.5f);
        y += 16;
        Text(b, T("terminal.caucion.title", new { rate = RateText(Broker.CaucionRate) }), area.X, y);
        y += 44;
        Text(b, T("terminal.caucion.term"), area.X, y + 10, Muted);
        int x = area.X + 140;
        foreach (int days in Broker.Rules.CaucionTerms)
        {
            int d = days;
            Button(b, new Rectangle(x, y, 110, 48), T("terminal.caucion.days", new { days = d }), () => _caucionDays = d, selected: _caucionDays == d);
            x += 118;
        }

        y += 62;
        DrawAmountControls(b, area.X, y, Broker.Cash);
        y += 62;
        (EconomicDate maturity, decimal interest) = Broker.CaucionTerms(_amount, _caucionDays, Date);
        Text(b, T("terminal.caucion.estimate", new { date = GameDateBridge.Format(I18n, maturity), total = Money(_amount + interest), interest = Money(interest) }), area.X, y, Muted);
        Button(b, new Rectangle(area.Right - 260, y - 14, 250, 56), T("terminal.caucion.place"), () =>
        {
            TradeError error = Broker.PlaceCaucion(_amount, _caucionDays, Date, Time);
            Result(error, T("terminal.caucion.done"));
        });
        y += 50;

        foreach (Caucion c in Broker.State.Cauciones)
        {
            Text(b, T("terminal.caucion.active", new { amount = Money(c.Principal), date = GameDateBridge.Format(I18n, c.Maturity), interest = Money(c.Interest) }), area.X, y);
            y += 32;
        }
    }

    private void DrawPortfolio(SpriteBatch b, Rectangle area)
    {
        int[] cols = { 0, 220, 360, 520, 680, 860, 1020 };
        string[] headers = { "terminal.col.instrument", "terminal.col.quantity", "terminal.col.cost", "terminal.col.price", "terminal.col.value", "terminal.col.result", "terminal.col.result-pct" };
        int y = area.Y;
        for (int i = 0; i < cols.Length; i++)
        {
            if (area.X + cols[i] < area.Right - 60)
            {
                Text(b, T(headers[i]), area.X + cols[i], y, Muted);
            }
        }

        y += 40;
        decimal totalValue = 0;
        decimal totalCost = 0;
        void Row(string name, Holding h, double price)
        {
            decimal value = (decimal)price * h.Quantity;
            decimal result = value - h.CostBasis;
            totalValue += value;
            totalCost += h.CostBasis;
            Color color = result >= 0 ? Positive : Negative;
            Text(b, name, area.X + cols[0], y);
            Text(b, h.Quantity.ToString("#,0", Formatting.Culture(I18n)), area.X + cols[1], y);
            Text(b, Price((double)h.AverageCost), area.X + cols[2], y);
            Text(b, Price(price), area.X + cols[3], y);
            Text(b, Money(value), area.X + cols[4], y);
            Text(b, Money(result), area.X + cols[5], y, color);
            if (area.X + cols[6] < area.Right - 60)
            {
                Text(b, Formatting.Percent(I18n, h.CostBasis > 0 ? (double)(result / h.CostBasis) : 0, signed: true), area.X + cols[6], y, color);
            }

            y += 40;
        }

        foreach ((string ticker, Holding h) in Broker.State.Stocks)
        {
            Row(ticker, h, _sim.Stocks.PriceAt(ticker, Time));
        }

        foreach ((string id, Holding h) in Broker.State.Letras)
        {
            if (Broker.FindLetra(id) is { } letra)
            {
                Row(id, h, Broker.LetraPrice(letra, Date));
            }
        }

        foreach (Caucion c in Broker.State.Cauciones)
        {
            Text(b, T("terminal.caucion.active", new { amount = Money(c.Principal), date = GameDateBridge.Format(I18n, c.Maturity), interest = Money(c.Interest) }), area.X, y);
            y += 36;
        }

        if (Broker.State.Stocks.Count + Broker.State.Letras.Count + Broker.State.Cauciones.Count == 0)
        {
            Text(b, T("terminal.portfolio.empty"), area.X, y, Muted);
            y += 40;
        }

        y += 20;
        b.Draw(Game1.staminaRect, new Rectangle(area.X, y, area.Width, 2), Muted * 0.5f);
        y += 16;
        decimal unrealized = totalValue - totalCost;
        Text(b, T("terminal.portfolio.totals", new
        {
            value = Money(Broker.PortfolioValue(Date, Time)),
            cash = Money(Broker.Cash),
            result = Money(unrealized),
        }), area.X, y, unrealized >= 0 ? Game1.textColor : Negative);
    }

    private void DrawAccount(SpriteBatch b, Rectangle area)
    {
        int y = area.Y;
        Text(b, T("terminal.account.wallet", new { gold = Money(Game1.player.Money) }), area.X, y);
        y += 40;
        Text(b, T("terminal.account.cash", new { cash = Money(Broker.Cash) }), area.X, y);
        y += 60;
        DrawAmountControls(b, area.X, y, Math.Max(Game1.player.Money, (long)Broker.Cash));
        y += 70;
        Button(b, new Rectangle(area.X, y, 260, 60), T("terminal.account.deposit"), () => Result(_actions.Deposit(_sim, _amount), T("terminal.account.deposited")));
        Button(b, new Rectangle(area.X + 280, y, 260, 60), T("terminal.account.withdraw"), () => Result(_actions.Withdraw(_sim, _amount), T("terminal.account.withdrawn")));
        y += 90;

        Text(b, T("terminal.account.trades"), area.X, y, Muted);
        y += 36;
        foreach (Trade t in Broker.State.Trades.Skip(Math.Max(0, Broker.State.Trades.Count - 8)).Reverse())
        {
            Text(b, T(t.Side == TradeSide.Buy ? "terminal.trade.buy" : "terminal.trade.sell", new
            {
                date = ShortDate(t.Date),
                time = Game1.getTimeOfDayString(t.TimeOfDay),
                quantity = t.Quantity,
                instrument = t.Instrument,
                price = Money(t.Price),
                net = Money(t.Net),
            }), area.X, y);
            y += 32;
        }
    }

    private void DrawAmountControls(SpriteBatch b, int x, int y, decimal max)
    {
        long[] steps = { -1000, -100, 100, 1000 };
        int cx = x;
        for (int i = 0; i < 2; i++)
        {
            long step = steps[i];
            Button(b, new Rectangle(cx, y, 100, 50), step.ToString("#,0", Formatting.Culture(I18n)), () => _amount = Math.Max(0, _amount + step));
            cx += 108;
        }

        b.Draw(Game1.staminaRect, new Rectangle(cx, y, 200, 50), Color.White * 0.7f);
        TextRight(b, Money(_amount), cx + 190, y + 10);
        cx += 212;
        for (int i = 2; i < 4; i++)
        {
            long step = steps[i];
            Button(b, new Rectangle(cx, y, 100, 50), "+" + step.ToString("#,0", Formatting.Culture(I18n)), () => _amount += step);
            cx += 108;
        }

        long all = (long)Math.Floor(max);
        Button(b, new Rectangle(cx, y, 120, 50), T("terminal.amount.all"), () => _amount = all);
    }

    // ───────────────────────────── Boleto de orden ─────────────────────────────

    private void OpenTicket(string instrument, TradeSide side)
    {
        _ticket = new Ticket { Instrument = instrument, Side = side, Quantity = 1 };
        _message = null;
    }

    private void DrawTicket(SpriteBatch b)
    {
        Ticket ticket = _ticket!;
        _overlayStart = _hotspots.Count;
        int w = 720;
        int h = 560;
        int x = xPositionOnScreen + ((width - w) / 2);
        int y = yPositionOnScreen + ((height - h) / 2);
        b.Draw(Game1.fadeToBlackRect, new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height), Color.Black * 0.35f);
        drawTextureBox(b, x, y, w, h, Color.White);
        Hotspot(new Rectangle(x, y, w, h), () => { }); // el boleto bloquea los clics de abajo

        int left = x + 36;
        int cy = y + 32;
        Button(b, new Rectangle(left, cy, 150, 52), T("terminal.buy"), () => ticket.Side = TradeSide.Buy, selected: ticket.Side == TradeSide.Buy);
        Button(b, new Rectangle(left + 160, cy, 150, 52), T("terminal.sell"), () => ticket.Side = TradeSide.Sell, selected: ticket.Side == TradeSide.Sell);
        Text(b, ticket.Instrument, left + 340, cy + 4, Game1.textColor, Game1.dialogueFont);
        cy += 76;

        Text(b, T("terminal.ticket.quantity"), left, cy + 12, Muted);
        int qx = left + 150;
        foreach (long step in new long[] { -100, -10, -1 })
        {
            long s = step;
            Button(b, new Rectangle(qx, cy, 70, 48), s.ToString(), () => ticket.Quantity = Math.Max(1, ticket.Quantity + s));
            qx += 76;
        }

        TextRight(b, ticket.Quantity.ToString("#,0", Formatting.Culture(I18n)), qx + 90, cy + 10);
        qx += 100;
        foreach (long step in new long[] { 1, 10, 100 })
        {
            long s = step;
            Button(b, new Rectangle(qx, cy, 70, 48), "+" + s, () => ticket.Quantity += s);
            qx += 76;
        }

        cy += 60;
        Button(b, new Rectangle(left + 150, cy, 200, 44), T("terminal.ticket.max"), () => ticket.Quantity = MaxQuantity(ticket));
        cy += 64;

        TradeQuote q = Broker.Quote(ticket.Instrument, ticket.Side, ticket.Quantity, Date, Time);
        var lines = new (string Label, decimal Value)[]
        {
            (T("terminal.ticket.unit"), q.UnitPrice),
            (T("terminal.ticket.gross"), q.Gross),
            (T("terminal.ticket.commission"), q.Commission),
            (T("terminal.ticket.fee"), q.MarketFee),
            (T("terminal.ticket.iva"), q.Iva),
        };
        foreach ((string label, decimal value) in lines)
        {
            Text(b, label, left, cy, Muted);
            TextRight(b, Money(value), x + w - 40, cy);
            cy += 34;
        }

        b.Draw(Game1.staminaRect, new Rectangle(left, cy, w - 76, 2), Muted * 0.5f);
        cy += 10;
        Text(b, T(ticket.Side == TradeSide.Buy ? "terminal.ticket.total-buy" : "terminal.ticket.total-sell"), left, cy);
        TextRight(b, Money(q.Total), x + w - 40, cy);
        cy += 44;
        if (!q.IsValid)
        {
            Text(b, T($"error.{q.Error}"), left, cy, Negative);
        }

        int by = y + h - 84;
        Button(b, new Rectangle(left, by, 300, 60), T("terminal.ticket.confirm"), () =>
        {
            TradeQuote done = Broker.Execute(ticket.Instrument, ticket.Side, ticket.Quantity, Date, Time);
            if (done.IsValid)
            {
                _ticket = null;
                Say(T("terminal.ticket.done", new { side = T(done.Side == TradeSide.Buy ? "terminal.bought" : "terminal.sold"), quantity = done.Quantity, instrument = done.Instrument, total = Money(done.Total) }), Positive);
            }
        }, enabled: q.IsValid);
        Button(b, new Rectangle(x + w - 36 - 240, by, 240, 60), T("terminal.ticket.cancel"), () => _ticket = null);
    }

    /// <summary>Máximo comprable con el saldo (o toda la tenencia, si se vende).</summary>
    private long MaxQuantity(Ticket ticket)
    {
        if (ticket.Side == TradeSide.Sell)
        {
            var book = _sim.Stocks.Find(ticket.Instrument) is null ? Broker.State.Letras : Broker.State.Stocks;
            return book.TryGetValue(ticket.Instrument, out Holding? h) ? Math.Max(1, h.Quantity) : 1;
        }

        long low = 1;
        long high = 1;
        while (high < 10_000_000 && Broker.Quote(ticket.Instrument, TradeSide.Buy, high, Date, Time).Total <= Broker.Cash)
        {
            low = high;
            high *= 2;
        }

        while (low + 1 < high)
        {
            long mid = (low + high) / 2;
            if (Broker.Quote(ticket.Instrument, TradeSide.Buy, mid, Date, Time).Total <= Broker.Cash)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    // ───────────────────────────── Utilidades de dibujo ─────────────────────────────

    private void Result(TradeError error, string success)
    {
        if (error == TradeError.None)
        {
            Say(success, Positive);
        }
        else
        {
            Say(T($"error.{error}"), Negative);
        }
    }

    private void Say(string message, Color color)
    {
        _message = message;
        _messageColor = color;
    }

    private void Hotspot(Rectangle bounds, Action action) => _hotspots.Add((bounds, action));

    private void Button(SpriteBatch b, Rectangle r, string label, Action action, bool enabled = true, bool selected = false)
    {
        bool hover = enabled && r.Contains(Game1.getMouseX(), Game1.getMouseY());
        Color tint = !enabled ? Color.Gray * 0.6f : selected ? Color.LightGreen : hover ? Color.Wheat : Color.White;
        drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9), r.X, r.Y, r.Width, r.Height, tint, 4f, false);
        Vector2 size = Game1.smallFont.MeasureString(label);
        Utility.drawTextWithShadow(b, label, Game1.smallFont,
            new Vector2(r.X + ((r.Width - size.X) / 2), r.Y + ((r.Height - size.Y) / 2) + 2),
            enabled ? Game1.textColor : Game1.textColor * 0.5f);
        if (enabled)
        {
            Hotspot(r, action);
        }
    }

    private static void Text(SpriteBatch b, string text, int x, int y, Color? color = null, SpriteFont? font = null) =>
        Utility.drawTextWithShadow(b, text, font ?? Game1.smallFont, new Vector2(x, y), color ?? Game1.textColor);

    private static void TextRight(SpriteBatch b, string text, int right, int y, Color? color = null) =>
        Text(b, text, right - (int)Game1.smallFont.MeasureString(text).X, y, color);

    private static string Ellipsis(string text, int maxWidth)
    {
        if (Game1.smallFont.MeasureString(text).X <= maxWidth)
        {
            return text;
        }

        while (text.Length > 1 && Game1.smallFont.MeasureString(text + "…").X > maxWidth)
        {
            text = text[..^1];
        }

        return text + "…";
    }

    private static IEnumerable<string> Wrap(string text, int maxWidth)
    {
        string line = "";
        foreach (string word in text.Split(' '))
        {
            string candidate = line.Length == 0 ? word : line + " " + word;
            if (Game1.smallFont.MeasureString(candidate).X > maxWidth && line.Length > 0)
            {
                yield return line;
                line = word;
            }
            else
            {
                line = candidate;
            }
        }

        if (line.Length > 0)
        {
            yield return line;
        }
    }

    /// <summary>Gráfico de línea simple con mínimo y máximo.</summary>
    private void Chart(SpriteBatch b, Rectangle area, IReadOnlyList<double> values, bool isIndex = false)
    {
        b.Draw(Game1.staminaRect, area, Color.Black * 0.08f);
        if (values.Count < 2)
        {
            Text(b, T("terminal.chart.no-data"), area.X + 10, area.Y + 10, Muted);
            return;
        }

        double min = values.Min();
        double max = values.Max();
        double range = Math.Max(max - min, max * 0.001);
        Color color = values[^1] >= values[0] ? Positive : Negative;
        Vector2 Point(int i) => new(
            area.X + 4 + ((area.Width - 8) * i / (float)(values.Count - 1)),
            area.Bottom - 6 - (float)((values[i] - min) / range * (area.Height - 12)));

        for (int i = 1; i < values.Count; i++)
        {
            Vector2 a = Point(i - 1);
            Vector2 c = Point(i);
            Vector2 d = c - a;
            b.Draw(Game1.staminaRect, new Rectangle((int)a.X, (int)a.Y, (int)Math.Ceiling(d.Length()), 3), null, color,
                (float)Math.Atan2(d.Y, d.X), Vector2.Zero, SpriteEffects.None, 0f);
        }

        string Label(double v) => isIndex ? Formatting.Number(I18n, v, "#,0.00") : Price(v);
        Text(b, Label(max), area.Right - 6 - (int)Game1.smallFont.MeasureString(Label(max)).X, area.Y + 2, Muted);
        Text(b, Label(min), area.Right - 6 - (int)Game1.smallFont.MeasureString(Label(min)).X, area.Bottom - 30, Muted);
    }

    /// <summary>"2 Ver. año 1": cabe en las tablas.</summary>
    private string ShortDate(EconomicDate date) =>
        T("date.short", new { day = date.Day, season = T($"season.short.{date.Season.ToString().ToLowerInvariant()}"), year = date.Year });

    private string Money(decimal amount) => amount.ToString("#,0.00", Formatting.Culture(I18n)) + "g";

    private string Money(long amount) => Money((decimal)amount);

    private string Price(double price) => price.ToString("#,0.00", Formatting.Culture(I18n)) + "g";

    private string RateText(double monthly) =>
        T("terminal.rate", new { monthly = Formatting.Percent(I18n, monthly), annual = Formatting.Percent(I18n, monthly * EconomicDate.SeasonsPerYear) });
}
