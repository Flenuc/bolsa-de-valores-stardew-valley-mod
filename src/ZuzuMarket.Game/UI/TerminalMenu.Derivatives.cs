using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Markets;
using ZuzuMarket.Core.Time;
using ZuzuMarket.Game.Bridges;

namespace ZuzuMarket.Game.UI;

/// <summary>Pestaña Derivados: futuros MATZU, cadena de opciones, Silo de Acopio y boleto de derivados.</summary>
internal sealed partial class TerminalMenu
{
    private int _derivativesTab;
    private string _selectedCommodity = "trigo";
    private string _selectedOptionTicker = "";
    private int _optionPeriod;

    private void DrawDerivatives(SpriteBatch b, Rectangle area)
    {
        string[] tabs = { "terminal.deriv.futures", "terminal.deriv.options" };
        int x = area.X;
        for (int i = 0; i < tabs.Length; i++)
        {
            int index = i;
            string label = T(tabs[i]);
            int w = (int)Game1.smallFont.MeasureString(label).X + 36;
            Button(b, new Rectangle(x, area.Y, w, 46), label, () => _derivativesTab = index, selected: _derivativesTab == i);
            x += w + 8;
        }

        string silo = T("terminal.silo.open");
        int sw = (int)Game1.smallFont.MeasureString(silo).X + 36;
        Button(b, new Rectangle(area.Right - sw, area.Y, sw, 46), silo, () =>
        {
            exitThisMenu(playSound: false);
            _actions.OpenSilo(_sim);
        });

        var inner = new Rectangle(area.X, area.Y + 62, area.Width, area.Height - 62);
        if (_derivativesTab == 0)
        {
            DrawFutures(b, inner);
        }
        else
        {
            DrawOptions(b, inner);
        }
    }

    private void DrawFutures(SpriteBatch b, Rectangle area)
    {
        // Lista de commodities a la izquierda.
        int listWidth = 230;
        int y = area.Y;
        foreach (CommodityDefinition c in _sim.Data.Commodities)
        {
            var row = new Rectangle(area.X - 8, y - 2, listWidth, 30);
            if (c.Id == _selectedCommodity)
            {
                b.Draw(Game1.staminaRect, row, Color.Wheat * 0.6f);
            }

            Hotspot(row, () => _selectedCommodity = c.Id);
            Text(b, Ellipsis(CommodityName(c), listWidth - 20), area.X, y);
            y += 30;
        }

        CommodityDefinition? commodity = _sim.Data.FindCommodity(_selectedCommodity);
        if (commodity is null)
        {
            return;
        }

        int x = area.X + listWidth + 20;
        int w = area.Right - x;
        int dy = area.Y;
        Text(b, T("terminal.futures.header", new
        {
            name = CommodityName(commodity),
            size = commodity.FuturesContractSize,
            silo = Broker.SiloUnits(commodity.Id).ToString("#,0", Formatting.Culture(I18n)),
            spot = Price(commodity.ReferencePrice * _sim.Market.Get(commodity.Id).Multiplier),
        }), x, dy);
        dy += 40;

        int[] cols = { 0, (int)(w * 0.17), (int)(w * 0.38), (int)(w * 0.52), (int)(w * 0.64), (int)(w * 0.76) };
        string[] headers = { "terminal.col.contract", "terminal.col.maturity", "terminal.col.price", "terminal.col.change", "terminal.col.position", "terminal.col.notional" };
        for (int i = 0; i < cols.Length; i++)
        {
            Text(b, T(headers[i]), x + cols[i], dy, Muted);
        }

        dy += 34;
        foreach (FuturesContract contract in Broker.FuturesFor(commodity.Id))
        {
            double price = Broker.FuturePrice(contract, Date);
            double change = contract.PreviousSettlement > 0 ? (contract.LastSettlement / contract.PreviousSettlement) - 1 : 0;
            long position = Broker.FuturesPositionFor(contract.Id)?.Quantity ?? 0;
            Text(b, contract.Id, x + cols[0], dy);
            Text(b, ShortDate(contract.Expiry), x + cols[1], dy);
            Text(b, Price(price), x + cols[2], dy);
            Text(b, Formatting.Percent(I18n, change, signed: true), x + cols[3], dy, change >= 0 ? Positive : Negative);
            Text(b, position == 0 ? "-" : position.ToString("+#,0;-#,0"), x + cols[4], dy, position == 0 ? Game1.textColor : position > 0 ? Positive : Negative);
            Text(b, Money((decimal)(price * commodity.FuturesContractSize)), x + cols[5], dy);
            dy += 36;
            Button(b, new Rectangle(x, dy, 140, 40), T("terminal.buy"), () => OpenTicket(contract.Id, TradeSide.Buy));
            Button(b, new Rectangle(x + 150, dy, 140, 40), T("terminal.sell"), () => OpenTicket(contract.Id, TradeSide.Sell));
            dy += 52;
        }

        dy += 6;
        Text(b, MarginSummary(), x, dy, Broker.Derivatives.MarginCallDeadline is null ? Muted : Negative);
        dy += 32;
        foreach (string line in Wrap(T("terminal.futures.help"), w).Take(5))
        {
            Text(b, line, x, dy, Muted);
            dy += 28;
        }
    }

    private void DrawOptions(SpriteBatch b, Rectangle area)
    {
        IReadOnlyList<CompanyDefinition> companies = _sim.Stocks.Companies;
        if (companies.All(c => c.Ticker != _selectedOptionTicker))
        {
            _selectedOptionTicker = companies.FirstOrDefault()?.Ticker ?? "";
        }

        // Especies y vencimientos.
        int x = area.X;
        foreach (CompanyDefinition c in companies)
        {
            Button(b, new Rectangle(x, area.Y, 96, 40), c.Ticker, () => _selectedOptionTicker = c.Ticker, selected: c.Ticker == _selectedOptionTicker);
            x += 102;
        }

        IReadOnlyList<FiscalPeriod> periods = Broker.OptionPeriods();
        if (periods.Count == 0)
        {
            return;
        }

        _optionPeriod = Math.Clamp(_optionPeriod, 0, periods.Count - 1);
        int y = area.Y + 52;
        x = area.X;
        for (int i = 0; i < periods.Count; i++)
        {
            int index = i;
            string label = T("terminal.options.expiry", new { date = ShortDate(Broker_ExpiryOf(periods[i])) });
            int w = (int)Game1.smallFont.MeasureString(label).X + 30;
            Button(b, new Rectangle(x, y, w, 40), label, () => _optionPeriod = index, selected: _optionPeriod == i);
            x += w + 8;
        }

        double spot = _sim.Stocks.PriceAt(_selectedOptionTicker, Time);
        Text(b, T("terminal.options.spot", new { ticker = _selectedOptionTicker, price = Price(spot), lot = Broker.DerivativesRules.OptionLot }), x + 20, y + 8, Muted);

        y += 56;
        int aw = area.Width;
        int[] cols = { 0, (int)(aw * 0.10), (int)(aw * 0.20), (int)(aw * 0.27), (int)(aw * 0.33), (int)(aw * 0.53), (int)(aw * 0.63), (int)(aw * 0.70), (int)(aw * 0.76) };
        Text(b, T("terminal.col.strike"), area.X + cols[0], y, Muted);
        Text(b, T("terminal.col.call-premium"), area.X + cols[1], y, Muted);
        Text(b, T("terminal.col.delta"), area.X + cols[2], y, Muted);
        Text(b, T("terminal.col.position-short"), area.X + cols[3], y, Muted);
        Text(b, T("terminal.col.put-premium"), area.X + cols[5], y, Muted);
        Text(b, T("terminal.col.delta"), area.X + cols[6], y, Muted);
        Text(b, T("terminal.col.position-short"), area.X + cols[7], y, Muted);
        y += 34;

        var chain = Broker.OptionsFor(_selectedOptionTicker, periods[_optionPeriod]);
        foreach (IGrouping<double, OptionSeries> strike in chain.GroupBy(o => o.Strike))
        {
            Color rowColor = Game1.textColor;
            Text(b, Price(strike.Key), area.X + cols[0], y, rowColor);
            foreach (OptionSeries series in strike)
            {
                bool call = series.Type == OptionType.Call;
                int baseCol = call ? 1 : 5;
                double premium = Broker.OptionPremium(series, Date, Time);
                long position = Broker.OptionPositionFor(series.Id)?.Quantity ?? 0;
                bool itm = call ? spot > series.Strike : series.Strike > spot;
                Text(b, Price(premium), area.X + cols[baseCol], y, itm ? Positive : Game1.textColor);
                Text(b, Formatting.Number(I18n, Broker.OptionDelta(series, Date, Time), "0.00"), area.X + cols[baseCol + 1], y, Muted);
                Text(b, position == 0 ? "-" : position.ToString("+#,0;-#,0"), area.X + cols[baseCol + 2], y);
                int bx = area.X + cols[baseCol + 3];
                Button(b, new Rectangle(bx, y - 6, 100, 38), T("terminal.options.buy-short"), () => OpenTicket(series.Id, TradeSide.Buy));
                Button(b, new Rectangle(bx + 106, y - 6, 100, 38), T("terminal.options.write-short"), () => OpenTicket(series.Id, TradeSide.Sell));
            }

            y += 44;
        }

        y += 6;
        Text(b, MarginSummary(), area.X, y, Broker.Derivatives.MarginCallDeadline is null ? Muted : Negative);
        y += 32;
        foreach (string line in Wrap(T("terminal.options.help"), area.Width).Take(4))
        {
            Text(b, line, area.X, y, Muted);
            y += 28;
        }
    }

    private void DrawDerivativeTicket(SpriteBatch b)
    {
        Ticket ticket = _ticket!;
        _overlayStart = _hotspots.Count;
        bool isFuture = Broker.FindFuture(ticket.Instrument) is not null;
        int w = 760;
        int h = 600;
        int x = xPositionOnScreen + ((width - w) / 2);
        int y = yPositionOnScreen + ((height - h) / 2);
        b.Draw(Game1.fadeToBlackRect, new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height), Color.Black * 0.35f);
        drawTextureBox(b, x, y, w, h, Color.White);
        Hotspot(new Rectangle(x, y, w, h), () => { });

        int left = x + 36;
        int cy = y + 32;
        string buyLabel = T(isFuture ? "terminal.buy" : "terminal.options.buy");
        string sellLabel = T(isFuture ? "terminal.sell" : "terminal.options.write");
        Button(b, new Rectangle(left, cy, 170, 52), buyLabel, () => ticket.Side = TradeSide.Buy, selected: ticket.Side == TradeSide.Buy);
        Button(b, new Rectangle(left + 180, cy, 170, 52), sellLabel, () => ticket.Side = TradeSide.Sell, selected: ticket.Side == TradeSide.Sell);
        Text(b, ticket.Instrument, left + 370, cy + 10);
        cy += 76;

        Text(b, T(isFuture ? "terminal.ticket.contracts" : "terminal.ticket.lots"), left, cy + 12, Muted);
        int qx = left + 170;
        foreach (long step in new long[] { -10, -1 })
        {
            long s = step;
            Button(b, new Rectangle(qx, cy, 70, 48), s.ToString(), () => ticket.Quantity = Math.Max(1, ticket.Quantity + s));
            qx += 76;
        }

        TextRight(b, ticket.Quantity.ToString("#,0", Formatting.Culture(I18n)), qx + 90, cy + 10);
        qx += 100;
        foreach (long step in new long[] { 1, 10 })
        {
            long s = step;
            Button(b, new Rectangle(qx, cy, 70, 48), "+" + s, () => ticket.Quantity += s);
            qx += 76;
        }

        cy += 70;
        DerivativeQuote q = isFuture
            ? Broker.QuoteFuture(ticket.Instrument, ticket.Side, ticket.Quantity, Date, Time)
            : Broker.QuoteOption(ticket.Instrument, ticket.Side, ticket.Quantity, Date, Time);
        var lines = new (string Label, string Value)[]
        {
            (T(isFuture ? "terminal.ticket.future-price" : "terminal.ticket.option-premium"), Price(q.UnitPrice)),
            (T(isFuture ? "terminal.ticket.notional" : "terminal.ticket.premium-total"), Money(q.Notional)),
            (T("terminal.ticket.commission-short"), Money(q.Commission)),
            (T("terminal.ticket.iva"), Money(q.Iva)),
            (T("terminal.ticket.margin-after"), Money(q.MarginAfter)),
            (T("terminal.ticket.margin-now"), Money(Broker.MarginBalance)),
        };
        foreach ((string label, string value) in lines)
        {
            Text(b, label, left, cy, Muted);
            TextRight(b, value, x + w - 40, cy);
            cy += 34;
        }

        foreach (string line in Wrap(T(isFuture
            ? (ticket.Side == TradeSide.Buy ? "terminal.ticket.future-buy-help" : "terminal.ticket.future-sell-help")
            : (ticket.Side == TradeSide.Buy ? "terminal.ticket.option-buy-help" : "terminal.ticket.option-write-help")), w - 72).Take(3))
        {
            Text(b, line, left, cy, Muted);
            cy += 28;
        }

        if (!q.IsValid)
        {
            Text(b, T($"error.{q.Error}"), left, cy + 4, Negative);
        }

        int by = y + h - 84;
        Button(b, new Rectangle(left, by, 300, 60), T("terminal.ticket.confirm"), () =>
        {
            DerivativeQuote done = isFuture
                ? Broker.ExecuteFuture(ticket.Instrument, ticket.Side, ticket.Quantity, Date, Time)
                : Broker.ExecuteOption(ticket.Instrument, ticket.Side, ticket.Quantity, Date, Time);
            if (done.IsValid)
            {
                _ticket = null;
                Say(T("terminal.ticket.derivative-done", new { quantity = done.Quantity, instrument = done.Instrument }), Positive);
            }
        }, enabled: q.IsValid);
        Button(b, new Rectangle(x + w - 36 - 240, by, 240, 60), T("terminal.ticket.cancel"), () => _ticket = null);
    }

    private string MarginSummary()
    {
        decimal required = Broker.RequiredMargin(Date, Time);
        string text = T("terminal.margin.summary", new
        {
            deposited = Money(Broker.MarginBalance),
            required = Money(required),
            minimum = Money(required * (decimal)Broker.DerivativesRules.MaintenanceRatio),
        });
        return Broker.Derivatives.MarginCallDeadline is { } deadline
            ? text + " · " + T("terminal.margin.call", new { date = ShortDate(deadline) })
            : text;
    }

    private string CommodityName(CommodityDefinition c)
    {
        var name = I18n.Get($"commodity.{c.Id}");
        return name.HasValue() ? name : c.Name;
    }

    private static EconomicDate Broker_ExpiryOf(FiscalPeriod period) => Broker.ExpiryOf(period);
}
