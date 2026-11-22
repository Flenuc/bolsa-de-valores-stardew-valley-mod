using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using ZuzuMarket.Core.Companies;
using ZuzuMarket.Game.Bridges;
using ZuzuMarket.Game.Content;

namespace ZuzuMarket.Game.UI;

/// <summary>Pestaña Empresas: tus sociedades (aportes, dividendos, transformación, disolución) y alta de sociedades nuevas.</summary>
internal sealed partial class TerminalMenu
{
    private string _selectedCompany = "";
    private long _companyAmount = 1_000;
    private string _newType = "agropecuaria";
    private string _newForm = "SRL";
    private long _newCapital = 5_000;
    private bool _confirmDissolve;
    private bool _capitalView;
    private long _bondAmount = 5_000;
    private int _bondTerm = 4;
    private int _bondInstallments = 1;
    private int _primaryPct = 10;
    private int _secondaryPct = 10;
    private long _shareBlock = 5_000;

    private PlayerCompanies Companies => _sim.Companies;

    private string CompanyName(CompanyState c) =>
        T("company.name", new { type = CompanyMail.TypeName(I18n, c.Type), form = T($"company.form.{c.Form}") });

    private void DrawCompanies(SpriteBatch b, Rectangle area)
    {
        int split = area.X + (int)(area.Width * 0.5);
        DrawOwnCompanies(b, new Rectangle(area.X, area.Y, split - area.X - 30, area.Height));
        var right = new Rectangle(split, area.Y, area.Right - split, area.Height);
        if (_capitalView && Companies.Find(_selectedCompany) is { Active: true } selected)
        {
            DrawCapitalMarket(b, right, selected);
        }
        else
        {
            _capitalView = false;
            DrawNewCompany(b, right);
        }
    }

    private void DrawOwnCompanies(SpriteBatch b, Rectangle area)
    {
        int y = area.Y;
        Text(b, T("terminal.companies.title"), area.X, y);
        y += 40;
        List<CompanyState> active = Companies.Active.ToList();
        if (active.Count == 0)
        {
            foreach (string line in Wrap(T("terminal.companies.none"), area.Width).Take(4))
            {
                Text(b, line, area.X, y, Muted);
                y += 30;
            }

            return;
        }

        if (active.All(c => c.Id != _selectedCompany))
        {
            _selectedCompany = active[0].Id;
        }

        // Selector compacto: hasta 2 sociedades por fila.
        int bw = (area.Width - 10) / 2;
        for (int i = 0; i < active.Count; i++)
        {
            string id = active[i].Id;
            Button(b, new Rectangle(area.X + ((i % 2) * (bw + 10)), y + ((i / 2) * 48), bw, 42), CompanyMail.TypeName(I18n, active[i].Type), () =>
            {
                _selectedCompany = id;
                _confirmDissolve = false;
            }, selected: _selectedCompany == id);
        }

        y += (((active.Count - 1) / 2) + 1) * 48;
        CompanyState selected = active.First(c => c.Id == _selectedCompany);
        foreach (string line in Wrap(T("terminal.companies.header", new { name = CompanyName(selected), rating = selected.Rating, cuit = selected.Cuit }), area.Width).Take(2))
        {
            Text(b, line, area.X, y, Muted);
            y += 30;
        }
        decimal unpaid = Companies.TaxesOf(selected).Unpaid.Sum(o => (decimal)o.Total);
        decimal distributable = Companies.Distributable(selected);
        bool srl = selected.Form.Equals("SRL", StringComparison.OrdinalIgnoreCase);
        decimal transformCost = Math.Max(0, Companies.MinimumCapital("SA") - Companies.CapitalOf(selected)) + Companies.TransformationFee;
        var rows = new List<(string Label, string Value)>
        {
            (T("terminal.companies.cash"), Money(Companies.Cash(selected))),
            (T("terminal.companies.revenue"), Money(Companies.AnnualRevenue(selected))),
            (T("terminal.companies.capital"), Money(Companies.CapitalOf(selected))),
            (T("terminal.companies.retained"), Money(Companies.RetainedEarnings(selected))),
            (T("terminal.companies.distributable"), Money(distributable)),
            (T("terminal.companies.unpaid"), Money(unpaid)),
        };
        if (srl)
        {
            rows.Add((T("terminal.companies.transform-cost"), Money(transformCost)));
        }

        foreach ((string label, string value) in rows)
        {
            Text(b, label, area.X, y, Muted);
            TextRight(b, value, area.Right, y);
            y += 28;
        }

        y += 8;
        DrawAmountControls(b, area.X, y, Math.Max(distributable, Game1.player.Money), () => _companyAmount, v => _companyAmount = v, area.Width);
        y += 58;
        int w = (area.Width - 10) / 2;
        string sid = selected.Id;
        Button(b, new Rectangle(area.X, y, w, 50), T("terminal.companies.contribute"), () =>
            CompanyResult(_actions.Contribute(_sim, sid, _companyAmount), T("terminal.companies.contributed")),
            enabled: _companyAmount >= 1 && _companyAmount <= Game1.player.Money);
        long dividend = (long)Math.Min(_companyAmount, distributable);
        Button(b, new Rectangle(area.X + w + 10, y, w, 50), T("terminal.companies.dividend"), () =>
            CompanyResult(_actions.PayDividend(_sim, sid, dividend), T("terminal.companies.dividend-done", new { amount = Money(dividend) })),
            enabled: dividend >= 1);
        y += 56;

        if (srl)
        {
            Button(b, new Rectangle(area.X, y, w, 50), T("terminal.companies.transform"), () =>
                CompanyResult(_actions.Transform(_sim, sid, "SA"), T("terminal.companies.transformed")),
                enabled: Game1.player.Money >= transformCost);
        }

        Button(b, new Rectangle(area.X + w + 10, y, w, 50), T(_confirmDissolve ? "terminal.companies.dissolve-confirm" : "terminal.companies.dissolve"), () =>
        {
            if (!_confirmDissolve)
            {
                _confirmDissolve = true;
                return;
            }

            _confirmDissolve = false;
            CompanyResult(_actions.Dissolve(_sim, sid), T("terminal.companies.dissolved"));
        });
        y += 56;

        Button(b, new Rectangle(area.X, y, w, 50), T(_capitalView ? "terminal.capital.back" : "terminal.capital.open"), () => _capitalView = !_capitalView,
            selected: _capitalView);
        if (Companies.Exporter?.Id == selected.Id)
        {
            int units = Companies.State.ExportContainer.Sum(i => i.Stack);
            Button(b, new Rectangle(area.X + w + 10, y, w, 50), T("terminal.companies.exports", new { units }), () =>
            {
                exitThisMenu();
                _actions.OpenExports(_sim);
            });
        }
    }
    private void DrawNewCompany(SpriteBatch b, Rectangle area)
    {
        int y = area.Y;
        Text(b, T("terminal.companies.new"), area.X, y);
        y += 40;
        int x = area.X;
        int col = 0;
        foreach (CompanyTypeDefinition type in _sim.Data.PlayerCompanies.Types)
        {
            string id = type.Id;
            bool exists = Companies.ActiveOfType(id) is not null;
            int bw = (area.Width - 10) / 2;
            Button(b, new Rectangle(x + (col * (bw + 10)), y, bw, 44), CompanyMail.TypeName(I18n, id), () => _newType = id,
                enabled: !exists, selected: _newType == id);
            col++;
            if (col == 2)
            {
                col = 0;
                y += 50;
            }

            if (_newType == id && exists)
            {
                _newType = _sim.Data.PlayerCompanies.Types.FirstOrDefault(t => Companies.ActiveOfType(t.Id) is null)?.Id ?? id;
            }
        }

        y += col == 0 ? 0 : 50;
        x = area.X;
        foreach (LegalFormDefinition form in _sim.Data.PlayerCompanies.Forms)
        {
            string id = form.Id;
            Button(b, new Rectangle(x, y, 150, 46), T($"company.form.{id}"), () => _newForm = id, selected: _newForm == id);
            x += 160;
        }

        y += 56;
        CompanyTypeDefinition? selected = _sim.Data.PlayerCompanies.FindType(_newType);
        if (selected is null)
        {
            return;
        }

        bool isUnlocked = Companies.State.UnlockAll || CompanyUnlocks.IsUnlocked(selected);
        foreach (string line in Wrap(T($"company.desc.{selected.Id}"), area.Width).Take(3))
        {
            Text(b, line, area.X, y, Muted);
            y += 28;
        }

        Text(b, T($"company.unlock.{selected.Unlock}", new { machines = CompanyUnlocks.MachinesNeeded }), area.X, y, isUnlocked ? Positive : Negative);
        y += 34;

        decimal minimum = Companies.MinimumCapital(_newForm);
        if (_newCapital < minimum)
        {
            _newCapital = (long)minimum;
        }

        DrawAmountControls(b, area.X, y, Game1.player.Money, () => _newCapital, v => _newCapital = v, area.Width);
        y += 60;
        decimal fee = Companies.RegistrationFee(_newForm);
        Text(b, T("terminal.companies.cost", new { minimum = Money(minimum), fee = Money(fee), total = Money(_newCapital + fee) }), area.X, y, Muted);
        y += 34;

        CompanyError error = Companies.CanCreate(_newType, _newForm, _newCapital, isUnlocked, Game1.player.Money);
        Button(b, new Rectangle(area.Right - 260, y, 260, 52), T("terminal.companies.create"), () =>
            CompanyResult(_actions.CreateCompany(_sim, _newType, _newForm, _newCapital), T("terminal.companies.created")),
            enabled: error == CompanyError.None);
        if (error != CompanyError.None)
        {
            foreach (string line in Wrap(T($"company.error.{error}"), area.Width - 280).Take(2))
            {
                Text(b, line, area.X, y, Negative);
                y += 28;
            }
        }
    }

    private void CompanyResult(CompanyError error, string success)
    {
        if (error == CompanyError.None)
        {
            Say(success, Positive);
        }
        else
        {
            Say(T($"company.error.{error}"), Negative);
        }
    }
}
