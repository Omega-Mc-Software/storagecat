using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace StorageCat;

public class UnitDialog : Form
{
    public Unit U = new();

    readonly TextBox nameT = new() { Width = 240 };
    readonly TextBox sizeT = new() { Width = 240 };
    readonly TextBox rentT = new() { Width = 240 };
    readonly TextBox tenantT = new() { Width = 240 };
    readonly TextBox phoneT = new() { Width = 240 };
    readonly TextBox moveInT = new() { Width = 240 };
    readonly TextBox notesT = new() { Width = 240, Multiline = true, Height = 60 };

    public UnitDialog(Unit? existing)
    {
        Text = existing == null ? "Add unit" : "Edit unit";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(400, 470);
        Font = Theme.BaseFont;

        if (existing != null)
        {
            U = existing;
            nameT.Text = U.Name;
            sizeT.Text = U.Size;
            rentT.Text = U.Rent == 0 ? "" : U.Rent.ToString("0.##");
            tenantT.Text = U.Tenant;
            phoneT.Text = U.Phone;
            moveInT.Text = U.MoveIn;
            notesT.Text = U.Notes;
        }

        var tlp = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 2,
        };
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(tlp, "Unit name *", nameT);
        AddRow(tlp, "Size", sizeT);
        AddRow(tlp, "Rent $/mo *", rentT);
        AddRow(tlp, "Tenant", tenantT);
        AddRow(tlp, "Phone", phoneT);
        AddRow(tlp, "Move-in date", Ui.DateWithPicker(moveInT));
        AddRow(tlp, "Notes", notesT);

        var hint = new Label
        {
            Text = "Move-in date format: 2026-09-13. With anniversary billing this day is the rent due day each month " +
                   "(day 29-31 counts as 28). Leave empty until someone rents it.",
            Tag = "muted",
            AutoSize = true,
            ForeColor = Theme.Muted,
            MaximumSize = new Size(340, 0),
        };
        tlp.Controls.Add(hint, 0, 7);
        tlp.SetColumnSpan(hint, 2);

        var ok = Ui.Btn("Save", 100, (_, _) => Save());
        var cancel = Ui.Btn("Cancel", 100, (_, _) => DialogResult = DialogResult.Cancel);
        var btns = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10),
        };
        btns.Controls.Add(cancel);
        btns.Controls.Add(ok);

        Controls.Add(tlp);
        Controls.Add(btns);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    static void AddRow(TableLayoutPanel tlp, string label, Control c)
    {
        var l = new Label { Text = label, TextAlign = ContentAlignment.MiddleLeft, AutoSize = true };
        c.Dock = DockStyle.Fill;
        tlp.Controls.Add(l);
        tlp.Controls.Add(c);
    }

    void Save()
    {
        if (string.IsNullOrWhiteSpace(nameT.Text))
        {
            MessageBox.Show("The unit needs a name (e.g. 10x10-01).", "StorageCat",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!Ui.ParseMoneyOrZero(rentT.Text, out var rent))
        {
            MessageBox.Show("Rent should be an amount like 95 or 95.00.", "StorageCat",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string moveIn = moveInT.Text.Trim();
        if (moveIn.Length > 0 && !Ui.ParseDate(moveIn, out _))
        {
            MessageBox.Show("Move-in date should look like 2026-09-13, or stay empty.", "StorageCat",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        U.Name = nameT.Text.Trim();
        U.Size = sizeT.Text.Trim();
        U.Rent = rent;
        U.Tenant = tenantT.Text.Trim();
        U.Phone = phoneT.Text.Trim();
        U.MoveIn = moveIn;
        U.Notes = notesT.Text.Trim();
        DialogResult = DialogResult.OK;
    }
}

public class TxnDialog : Form
{
    public Txn? T;

    readonly string kind;
    readonly TextBox dateT = new() { Width = 240 };
    readonly ComboBox unitC = new() { Width = 240, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly TextBox catT = new() { Width = 240 };
    readonly TextBox amountT = new() { Width = 240 };
    readonly TextBox noteT = new() { Width = 240, Multiline = true, Height = 60 };
    readonly List<Unit> units;
    readonly List<long?> unitIds = new();

    public TxnDialog(string kind, List<Unit> units, long? preselect = null)
    {
        this.kind = kind;
        this.units = units;

        Text = kind switch
        {
            "rent" => "Record rent (money in)",
            "late" => "Add late fee (money in)",
            "hold" => "Record hold fee (money in)",
            _ => "Add expense (money out)",
        };
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(400, 400);
        Font = Theme.BaseFont;

        dateT.Text = Ui.Today();
        unitIds.Add(null);
        unitC.Items.Add("(no unit)");
        foreach (var u in units)
        {
            unitIds.Add(u.Id);
            unitC.Items.Add(u.Name);
        }
        int sel = 0;
        if (preselect.HasValue)
            for (int i = 0; i < unitIds.Count; i++)
                if (unitIds[i] == preselect.Value) { sel = i; break; }
        unitC.SelectedIndex = sel;

        var tlp = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 2,
        };
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(tlp, "Date *", Ui.DateWithPicker(dateT));
        AddRow(tlp, "Unit", unitC);
        AddRow(tlp, "Category", catT);
        AddRow(tlp, "Amount $ *", amountT);
        AddRow(tlp, "Note", noteT);

        var catHint = new Label
        {
            Text = kind switch
            {
                "rent" => "Tip: category can be which month this rent covers, e.g. \"September\".",
                "late" => "The late fee amount from Settings is just the default — type what was actually charged.",
                "hold" => "A hold fee taken from someone on the waitlist who said yes.",
                _ => "e.g. lock, repair, supplies, insurance, tax.",
            },
            Tag = "muted",
            AutoSize = true,
            ForeColor = Theme.Muted,
            MaximumSize = new Size(340, 0),
        };
        tlp.Controls.Add(catHint, 1, 5);

        var ok = Ui.Btn("Save", 100, (_, _) => Save());
        var cancel = Ui.Btn("Cancel", 100, (_, _) => DialogResult = DialogResult.Cancel);
        var btns = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10),
        };
        btns.Controls.Add(cancel);
        btns.Controls.Add(ok);

        Controls.Add(tlp);
        Controls.Add(btns);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    static void AddRow(TableLayoutPanel tlp, string label, Control c)
    {
        var l = new Label { Text = label, TextAlign = ContentAlignment.MiddleLeft, AutoSize = true };
        c.Dock = DockStyle.Fill;
        tlp.Controls.Add(l);
        tlp.Controls.Add(c);
    }

    void Save()
    {
        if (!Ui.ParseDate(dateT.Text, out var d))
        {
            MessageBox.Show("Date should look like 2026-09-13.", "StorageCat",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!Ui.ParseMoney(amountT.Text, out var amount))
        {
            MessageBox.Show("Amount must be a number greater than 0.", "StorageCat",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var idx = unitC.SelectedIndex;
        T = new Txn
        {
            Date = d.ToString("yyyy-MM-dd"),
            UnitId = idx >= 0 ? unitIds[idx] : null,
            Kind = kind,
            Category = catT.Text.Trim(),
            Amount = amount,
            Note = noteT.Text.Trim(),
        };
        DialogResult = DialogResult.OK;
    }
}

public class WaitDialog : Form
{
    public WaitEntry? W;

    readonly TextBox dateT = new() { Width = 240 };
    readonly TextBox nameT = new() { Width = 240 };
    readonly TextBox phoneT = new() { Width = 240 };
    readonly ComboBox sizeC = new() { Width = 240, DropDownStyle = ComboBoxStyle.DropDown };
    readonly CheckBox holdPaid = new() { Text = "Hold fee already paid", AutoSize = true };
    readonly TextBox notesT = new() { Width = 240, Multiline = true, Height = 60 };

    public WaitDialog(WaitEntry? existing)
    {
        Text = existing == null ? "Add to waitlist" : "Edit waitlist entry";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(400, 440);
        Font = Theme.BaseFont;

        dateT.Text = Ui.Today();

        foreach (var s in Db.GetSizes())
            if (s.Name.Trim().Length > 0 && !sizeC.Items.Contains(s.Name.Trim()))
                sizeC.Items.Add(s.Name.Trim());

        if (existing != null)
        {
            W = existing;
            dateT.Text = W.Created;
            nameT.Text = W.Name;
            phoneT.Text = W.Phone;
            sizeC.Text = W.SizeWanted;
            holdPaid.Checked = W.HoldPaid;
            notesT.Text = W.Notes;
        }
        else
        {
            holdPaid.Checked = Billing.HoldFeeOn;
        }

        var tlp = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 2,
        };
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(tlp, "Added *", Ui.DateWithPicker(dateT));
        AddRow(tlp, "Name *", nameT);
        AddRow(tlp, "Phone", phoneT);
        AddRow(tlp, "Size wanted", sizeC);
        tlp.Controls.Add(new Label(), 0, 4);
        tlp.Controls.Add(holdPaid, 1, 4);
        AddRow(tlp, "Notes", notesT);

        var hint = new Label
        {
            Text = Billing.HoldFeeOn
                ? $"Your hold fee setting is {Billing.HoldFeeAmt.ToString("0.##")} — tick the box once they've paid it."
                : "Hold fees are off in Settings; the tick box still works if you take one informally.",
            Tag = "muted",
            AutoSize = true,
            ForeColor = Theme.Muted,
            MaximumSize = new Size(340, 0),
        };
        tlp.Controls.Add(hint, 0, 6);
        tlp.SetColumnSpan(hint, 2);

        var ok = Ui.Btn("Save", 100, (_, _) => Save());
        var cancel = Ui.Btn("Cancel", 100, (_, _) => DialogResult = DialogResult.Cancel);
        var btns = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10),
        };
        btns.Controls.Add(cancel);
        btns.Controls.Add(ok);

        Controls.Add(tlp);
        Controls.Add(btns);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    static void AddRow(TableLayoutPanel tlp, string label, Control c)
    {
        var l = new Label { Text = label, TextAlign = ContentAlignment.MiddleLeft, AutoSize = true };
        c.Dock = DockStyle.Fill;
        tlp.Controls.Add(l);
        tlp.Controls.Add(c);
    }

    void Save()
    {
        if (!Ui.ParseDate(dateT.Text, out var d))
        {
            MessageBox.Show("Date should look like 2026-09-13.", "StorageCat",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(nameT.Text))
        {
            MessageBox.Show("A name (or at least a contact) is needed.", "StorageCat",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        W = new WaitEntry
        {
            Created = d.ToString("yyyy-MM-dd"),
            Name = nameT.Text.Trim(),
            Phone = phoneT.Text.Trim(),
            SizeWanted = sizeC.Text.Trim(),
            HoldPaid = holdPaid.Checked,
            Notes = notesT.Text.Trim(),
        };
        DialogResult = DialogResult.OK;
    }
}
