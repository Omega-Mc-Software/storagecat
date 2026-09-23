using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace StorageCat;

public static class Ui
{
    public static DataGridViewTextBoxColumn Col(string header, int width,
        DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft)
    {
        return new DataGridViewTextBoxColumn
        {
            HeaderText = header,
            Width = width,
            DefaultCellStyle = { Alignment = align },
        };
    }

    public static Button Btn(string text, int width = 110, EventHandler? onClick = null)
    {
        // 32px: default 23px clips the bottom of 9.75pt Segoe UI text (found by Dad on LedgerCat v1.0)
        var b = new Button { Text = text, Width = width, Height = 32 };
        if (onClick != null) b.Click += onClick;
        return b;
    }

    public static string Today() => DateTime.Today.ToString("yyyy-MM-dd");

    public static bool ParseDate(string? s, out DateOnly d) =>
        DateOnly.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out d);

    /// A typed date box with a calendar picker beside it. Either one edits the same value.
    public static Panel DateWithPicker(TextBox tb, int textWidth = 130)
    {
        bool syncing = false;
        var dtp = new DateTimePicker
        {
            Format = DateTimePickerFormat.Short,
            Width = 110,
            Left = textWidth + 6,
            Top = 1,
        };
        tb.Width = textWidth;
        tb.TextChanged += (_, _) =>
        {
            if (syncing) return;
            if (ParseDate(tb.Text, out var d))
            {
                syncing = true;
                dtp.Value = d.ToDateTime(TimeOnly.MinValue);
                syncing = false;
            }
        };
        dtp.ValueChanged += (_, _) =>
        {
            if (syncing) return;
            syncing = true;
            tb.Text = dtp.Value.ToString("yyyy-MM-dd");
            syncing = false;
        };
        if (ParseDate(tb.Text, out var d0))
            dtp.Value = d0.ToDateTime(TimeOnly.MinValue);
        var p = new Panel { Width = textWidth + 6 + 110, Height = 27, Margin = new Padding(0) };
        p.Controls.Add(tb);
        p.Controls.Add(dtp);
        return p;
    }

    public static bool ParseMoney(string? s, out decimal v)
    {
        v = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        return decimal.TryParse(s.Trim().Replace("$", "").Replace(",", ""),
            NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0;
    }

    public static bool ParseMoneyOrZero(string? s, out decimal v)
    {
        if (string.IsNullOrWhiteSpace(s)) { v = 0; return true; }
        return ParseMoney(s, out v) || (s!.Trim() is "0" or "0.00" && (v = 0) == 0);
    }

    public static Panel TopBar(params Control[] buttons)
    {
        var p = new Panel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(12, 8, 12, 8) };
        int x = 0;
        foreach (var b in buttons)
        {
            b.Location = new Point(x, 8);
            p.Controls.Add(b);
            x += b.Width + 8;
        }
        return p;
    }

    public static DataGridView MakeGrid()
    {
        return new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoGenerateColumns = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        };
    }
}

// ---------- billing engine ----------

public static class Billing
{
    public static string Mode => Db.GetSetting("billing_mode", "anniversary"); // first | anniversary
    public static int GraceDays => Math.Max(0, Db.GetIntSetting("grace_days", 0));
    public static decimal LateFee => Db.GetDecSetting("late_fee", 0m);
    public static bool HoldFeeOn => Db.GetSetting("hold_fee_on", "0") == "1";
    public static decimal HoldFeeAmt => Db.GetDecSetting("hold_fee_amt", 0m);
    public static bool ProrateOn => Db.GetSetting("prorate_on", "0") == "1";

    public static int DueDay(Unit u)
    {
        if (Mode == "first") return 1;
        if (Ui.ParseDate(u.MoveIn, out var d)) return Math.Min(d.Day, 28);
        return 1;
    }

    // Rent due for a given month. Prorates the FIRST month for mid-month move-ins
    // under first-of-month billing when the tick is on (Dad, v0.1.0 test):
    // rent × days left in the move-in month, move-in day included, rounded to the cent.
    public static decimal RentDue(Unit u, DateOnly monthStart)
    {
        if (Mode != "first" || !ProrateOn) return u.Rent;
        if (!Ui.ParseDate(u.MoveIn, out var mi)) return u.Rent;
        if (mi.Year != monthStart.Year || mi.Month != monthStart.Month) return u.Rent;
        if (mi.Day <= 1) return u.Rent;
        int dim = DateTime.DaysInMonth(mi.Year, mi.Month);
        int daysLeft = dim - mi.Day + 1;
        return Math.Round(u.Rent * daysLeft / dim, 2, MidpointRounding.AwayFromZero);
    }

    public static decimal PaidInMonth(List<Txn> txns, long unitId, string monthKey)
    {
        decimal sum = 0;
        foreach (var t in txns)
            if (t.UnitId == unitId && (t.Kind == "rent" || t.Kind == "late") &&
                t.Date.Length >= 7 && t.Date[..7] == monthKey)
                sum += t.Amount;
        return sum;
    }

    // rent status for the current month: Paid / Due N / LATE + days late.
    // A recorded late fee counts toward the balance — rent alone doesn't read Paid
    // once the late fee is due (Dad, v0.1.0 test).
    public static (string Status, int DaysLate) Status(Unit u, List<Txn> txns, DateOnly today)
    {
        if (!u.Occupied || u.Rent <= 0) return ("", 0);
        // Move-in in the future = lease hasn't started, no rent owed yet (Dad, v0.1.1 test:
        // move-in 10-01 with today 9-13 showed 6 days late)
        if (Ui.ParseDate(u.MoveIn, out var starts) && starts > today)
            return ($"Starts {u.MoveIn}", 0);
        string monthKey = today.ToString("yyyy-MM");
        decimal rentDue = RentDue(u, new DateOnly(today.Year, today.Month, 1));
        var due = new DateOnly(today.Year, today.Month, DueDay(u));
        var graceEnd = due.AddDays(GraceDays);
        bool isLate = today > graceEnd;
        decimal dueNow = rentDue + (isLate ? LateFee : 0m);
        if (PaidInMonth(txns, u.Id, monthKey) >= dueNow) return ("Paid ✓", 0);
        if (isLate)
            return (LateFee > 0 ? "LATE +fee" : "LATE", today.DayNumber - graceEnd.DayNumber + 1);
        return ($"Due {DueDay(u)}", 0);
    }

    // Last month only counts once the lease actually reaches back that far —
    // a fresh move-in doesn't owe last month (Dad, v0.1.0 test).
    public static string PrevMonthStatus(Unit u, List<Txn> txns, DateOnly today)
    {
        if (!u.Occupied || u.Rent <= 0) return "";
        var prev = today.AddMonths(-1);
        if (Ui.ParseDate(u.MoveIn, out var mi))
        {
            var prevEnd = new DateOnly(prev.Year, prev.Month, DateTime.DaysInMonth(prev.Year, prev.Month));
            if (mi > prevEnd) return ""; // lease started this month or later
        }
        return PaidInMonth(txns, u.Id, prev.ToString("yyyy-MM")) >= RentDue(u, new DateOnly(prev.Year, prev.Month, 1))
            ? "Paid" : "DUE";
    }

    // What this unit owes right now: any unpaid current-month balance (incl. late
    // fee once past grace) plus any unpaid last-month balance the lease reaches.
    public static decimal Owed(Unit u, List<Txn> txns, DateOnly today)
    {
        if (!u.Occupied || u.Rent <= 0) return 0m;
        // future move-in owes nothing yet (Dad, v0.1.1 test)
        if (Ui.ParseDate(u.MoveIn, out var starts) && starts > today) return 0m;
        decimal owed = 0;
        var (status, _) = Status(u, txns, today);
        string monthKey = today.ToString("yyyy-MM");
        if (status != "Paid ✓")
        {
            decimal rentDue = RentDue(u, new DateOnly(today.Year, today.Month, 1));
            var due = new DateOnly(today.Year, today.Month, DueDay(u));
            var graceEnd = due.AddDays(GraceDays);
            decimal lateNow = today > graceEnd ? LateFee : 0m;
            owed += Math.Max(0, rentDue + lateNow - PaidInMonth(txns, u.Id, monthKey));
        }
        var prev = today.AddMonths(-1);
        if (PrevMonthStatus(u, txns, today) == "DUE")
            owed += Math.Max(0, RentDue(u, new DateOnly(prev.Year, prev.Month, 1)) - PaidInMonth(txns, u.Id, prev.ToString("yyyy-MM")));
        return owed;
    }

    public static string Bucket(int daysLate)
    {
        if (daysLate <= 30) return "1-30";
        if (daysLate <= 60) return "31-60";
        if (daysLate <= 90) return "61-90";
        return "90+";
    }
}

// ---------- Units tab ----------

public class UnitsTab : UserControl
{
    readonly DataGridView grid = Ui.MakeGrid();
    readonly Label summary = new()
    {
        Dock = DockStyle.Top,
        Height = 34,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(14, 6, 4, 0),
    };
    readonly Button add, gen, editB, rentB, outB, delBtn, delViewBtn, undoBtn, purgeBtn;
    bool showDeleted = false;

    public UnitsTab()
    {
        add = Ui.Btn("Add unit", 100, (_, _) => Edit(null));
        gen = Ui.Btn("Generate units", 130, (_, _) => Generate());
        editB = Ui.Btn("Edit", 80, (_, _) => EditSelected());
        rentB = Ui.Btn("Record rent", 120, (_, _) => RecordRent());
        outB = Ui.Btn("Move out", 100, (_, _) => MoveOut());
        delBtn = Ui.Btn("Delete", 90, (_, _) => DeleteSelected());
        delViewBtn = Ui.Btn("Deleted", 90, (_, _) => { showDeleted = !showDeleted; RefreshData(); });
        undoBtn = Ui.Btn("Undo delete", 110, (_, _) => UndoSelected());
        purgeBtn = Ui.Btn("Delete forever", 120, (_, _) => PurgeSelected());

        // column order per Dad, v0.1.2 test: Unit, Tenant, Phone, Due now, This month,
        // Last month, Days late, Move-in, Aging, Notes (icon only — full text on hover)
        grid.Columns.Add(Ui.Col("Unit", 120));
        grid.Columns.Add(Ui.Col("Tenant", 150));
        grid.Columns.Add(Ui.Col("Phone", 110));
        grid.Columns.Add(Ui.Col("Due now", 95, DataGridViewContentAlignment.MiddleRight));
        grid.Columns.Add(Ui.Col("This month", 95, DataGridViewContentAlignment.MiddleRight));
        grid.Columns.Add(Ui.Col("Last month", 95, DataGridViewContentAlignment.MiddleRight));
        grid.Columns.Add(Ui.Col("Days late", 80, DataGridViewContentAlignment.MiddleRight));
        grid.Columns.Add(Ui.Col("Move-in", 100, DataGridViewContentAlignment.MiddleRight));
        grid.Columns.Add(Ui.Col("Aging", 70, DataGridViewContentAlignment.MiddleRight));
        grid.Columns.Add(Ui.Col("Notes", 46));

        Controls.Add(grid);
        Controls.Add(Ui.TopBar(add, gen, editB, rentB, outB, delBtn, delViewBtn, undoBtn, purgeBtn));
        Controls.Add(summary);

        grid.CellDoubleClick += (_, _) => { if (!showDeleted) EditSelected(); };
    }

    void UndoSelected()
    {
        if (grid.SelectedRows.Count == 0) return;
        if (grid.SelectedRows[0].Tag is not long id) return;
        Db.RestoreUnit(id);
        RefreshData();
    }

    void PurgeSelected()
    {
        if (grid.SelectedRows.Count == 0) return;
        if (grid.SelectedRows[0].Tag is not long id) return;
        if (MessageBox.Show(
                "Delete this unit FOREVER?\n\nPast transactions are kept on the money tab but lose the unit link. This cannot be undone.",
                "Delete forever", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        Db.DeleteUnit(id);
        RefreshData();
    }

    void EditSelected()
    {
        if (grid.SelectedRows.Count == 0) return;
        if (grid.SelectedRows[0].Tag is long id)
        {
            var u = Db.ListUnits().FirstOrDefault(x => x.Id == id);
            if (u != null) Edit(u);
        }
    }

    void Edit(Unit? existing)
    {
        using var dlg = new UnitDialog(existing);
        if (dlg.ShowDialog(FindForm()) == DialogResult.OK)
        {
            Db.SaveUnit(dlg.U);
            RefreshData();
        }
    }

    void RecordRent()
    {
        long? unitId = null;
        if (grid.SelectedRows.Count > 0 && grid.SelectedRows[0].Tag is long id) unitId = id;
        using var dlg = new TxnDialog("rent", Db.ListUnits(), unitId);
        if (dlg.ShowDialog(FindForm()) == DialogResult.OK && dlg.T != null)
        {
            Db.SaveTxn(dlg.T);
            RefreshData();
        }
    }

    void MoveOut()
    {
        if (grid.SelectedRows.Count == 0) return;
        if (grid.SelectedRows[0].Tag is not long id) return;
        var u = Db.ListUnits().FirstOrDefault(x => x.Id == id);
        if (u == null) return;
        if (!u.Occupied)
        {
            MessageBox.Show("That unit has no tenant to move out.", "StoragePaw",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(
                $"Move {u.Tenant} out of {u.Name}?\n\nThe unit keeps its rent and size — just the tenant, phone and move-in date are cleared. Past payments stay on the money tab.",
                "Move out", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        u.Tenant = "";
        u.Phone = "";
        u.MoveIn = "";
        Db.SaveUnit(u);
        RefreshData();
    }

    void Generate()
    {
        var sizes = Db.GetSizes().Where(s => s.Qty > 0).ToList();
        if (sizes.Count == 0)
        {
            MessageBox.Show("Add sizes and quantities in the Settings tab first.",
                "Generate units", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        int total = sizes.Sum(s => s.Qty);
        if (MessageBox.Show(
                $"Create up to {total} unit(s) from your size list?\n\nExisting units with the same name are left alone, so this is safe to run twice.",
                "Generate units", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        var existing = Db.ListUnits().Select(u => u.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int created = 0;
        foreach (var s in sizes)
        {
            string stem = CleanName(s.Name);
            for (int i = 1; i <= s.Qty; i++)
            {
                string name = $"{stem}-{i:D2}";
                if (existing.Contains(name)) continue;
                Db.SaveUnit(new Unit { Name = name, Size = s.Name.Trim(), Rent = s.Price });
                existing.Add(name);
                created++;
            }
        }
        RefreshData();
        MessageBox.Show(created > 0
                ? $"Created {created} unit(s)."
                : "Nothing to create — all those unit names already exist.",
            "Generate units", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    static string CleanName(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in s.Trim())
            sb.Append(char.IsWhiteSpace(c) ? '-' : c);
        return sb.Length == 0 ? "unit" : sb.ToString();
    }

    void DeleteSelected()
    {
        if (grid.SelectedRows.Count == 0) return;
        if (grid.SelectedRows[0].Tag is not long id) return;
        if (MessageBox.Show("Delete this unit?\n\nIt moves to the Deleted view (Deleted button) where you can undo it. Past transactions are kept.",
                "Delete unit", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        Db.SoftDeleteUnit(id);
        RefreshData();
    }

    public void RefreshData()
    {
        grid.SuspendLayout();
        long? keepId = grid.SelectedRows.Count > 0 && grid.SelectedRows[0].Tag is long k ? k : null;
        grid.Rows.Clear();
        var txns = Db.ListTxns();
        var today = DateOnly.FromDateTime(DateTime.Today);
        int occupied = 0, late = 0, total = 0;
        decimal owedTotal = 0;

        foreach (var u in Db.ListUnits(showDeleted))
        {
            total++;
            if (u.Occupied) occupied++;
            var (status, daysLate) = Billing.Status(u, txns, today);
            string prev = Billing.PrevMonthStatus(u, txns, today);
            string bucket = daysLate > 0 ? Billing.Bucket(daysLate) : "";
            decimal owed = Billing.Owed(u, txns, today);
            owedTotal += owed;
            if (daysLate > 0) late++;

            string notesIcon = string.IsNullOrWhiteSpace(u.Notes) ? "" : "📝";
            var rowIdx = grid.Rows.Add(u.Name, u.Tenant, u.Phone,
                owed > 0 ? Theme.Money(owed) : "", status, prev,
                daysLate > 0 ? daysLate.ToString() : "", u.MoveIn, bucket, notesIcon);
            var row = grid.Rows[rowIdx];
            row.Tag = u.Id;
            if (notesIcon.Length > 0) row.Cells[9].ToolTipText = u.Notes;

            // shade the paid/late box, not the text only (Dad, v0.1.0 test)
            var cell = row.Cells[4];
            if (status == "Paid ✓")
            {
                cell.Style.BackColor = Theme.GoodBg;
                cell.Style.ForeColor = Theme.Good;
                cell.Style.SelectionBackColor = Theme.Accent;
                cell.Style.SelectionForeColor = Color.White;
            }
            else if (daysLate > 0)
            {
                cell.Style.BackColor = Theme.BadBg;
                cell.Style.ForeColor = Theme.Text;
                cell.Style.SelectionBackColor = Theme.Accent;
                cell.Style.SelectionForeColor = Color.White;
            }
            var prevCell = row.Cells[5];
            if (prev == "DUE")
            {
                prevCell.Style.BackColor = Theme.BadBg;
                prevCell.Style.ForeColor = Theme.Text;
                prevCell.Style.SelectionBackColor = Theme.Accent;
            }
            else if (prev == "Paid")
            {
                prevCell.Style.BackColor = Theme.GoodBg;
                prevCell.Style.ForeColor = Theme.Good;
                prevCell.Style.SelectionBackColor = Theme.Accent;
            }

            if (keepId.HasValue && u.Id == keepId.Value) row.Selected = true;
        }
        grid.ResumeLayout();

        add.Visible = gen.Visible = editB.Visible = rentB.Visible = outB.Visible = delBtn.Visible = !showDeleted;
        undoBtn.Visible = purgeBtn.Visible = showDeleted;

        if (showDeleted)
        {
            summary.Text = $"Showing {total} deleted unit(s). Select one and use Undo delete to bring it back, or Delete forever to remove it for good.";
            return;
        }

        decimal potential = Db.ListUnits().Where(x => x.Occupied).Sum(x => x.Rent);
        summary.Text =
            $"Occupancy: {occupied}/{total} ({(total == 0 ? 0 : occupied * 100 / total)}%)   ·   " +
            $"late: {late}   ·   " +
            $"total owed: {Theme.Money(owedTotal)}   ·   " +
            $"billed monthly if all rent comes in: {Theme.Money(potential)}";
    }
}

// ---------- Money tab ----------

public class MoneyTab : UserControl
{
    readonly DataGridView grid = Ui.MakeGrid();
    readonly Label summary = new()
    {
        Dock = DockStyle.Top,
        Height = 58,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(14, 8, 4, 0),
    };
    readonly Button addRent, addLate, addHold, addExp, delBtn, delViewBtn, undoBtn, purgeBtn;
    bool showDeleted = false;

    public MoneyTab()
    {
        addRent = Ui.Btn("Record rent", 120, (_, _) => AddTxn("rent"));
        addLate = Ui.Btn("Add late fee", 120, (_, _) => AddTxn("late"));
        addHold = Ui.Btn("Record hold fee", 140, (_, _) => AddTxn("hold"));
        addExp = Ui.Btn("Add expense", 120, (_, _) => AddTxn("expense"));
        delBtn = Ui.Btn("Delete", 90, (_, _) => DeleteSelected());
        delViewBtn = Ui.Btn("Deleted", 90, (_, _) => { showDeleted = !showDeleted; RefreshData(); });
        undoBtn = Ui.Btn("Undo delete", 110, (_, _) => UndoSelected());
        purgeBtn = Ui.Btn("Delete forever", 120, (_, _) => PurgeSelected());

        grid.Columns.Add(Ui.Col("Date", 100));
        grid.Columns.Add(Ui.Col("Unit", 120));
        grid.Columns.Add(Ui.Col("Type", 90));
        grid.Columns.Add(Ui.Col("Category", 130));
        grid.Columns.Add(Ui.Col("Amount", 110, DataGridViewContentAlignment.MiddleRight));
        grid.Columns.Add(Ui.Col("Note", 260));

        Controls.Add(grid);
        Controls.Add(Ui.TopBar(addRent, addLate, addHold, addExp, delBtn, delViewBtn, undoBtn, purgeBtn));
        Controls.Add(summary);
    }

    void UndoSelected()
    {
        if (grid.SelectedRows.Count == 0) return;
        if (grid.SelectedRows[0].Tag is not long id) return;
        Db.RestoreTxn(id);
        RefreshData();
    }

    void PurgeSelected()
    {
        if (grid.SelectedRows.Count == 0) return;
        if (grid.SelectedRows[0].Tag is not long id) return;
        if (MessageBox.Show("Delete this entry FOREVER? This cannot be undone.", "Delete forever",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        Db.DeleteTxn(id);
        RefreshData();
    }

    void AddTxn(string kind)
    {
        long? unitId = null;
        using var dlg = new TxnDialog(kind, Db.ListUnits(), unitId);
        if (dlg.ShowDialog(FindForm()) == DialogResult.OK && dlg.T != null)
        {
            Db.SaveTxn(dlg.T);
            RefreshData();
        }
    }

    void DeleteSelected()
    {
        if (grid.SelectedRows.Count == 0) return;
        if (grid.SelectedRows[0].Tag is not long id) return;
        if (MessageBox.Show("Delete this entry?\n\nIt moves to the Deleted view (Deleted button) where you can undo it.", "Delete entry",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        Db.SoftDeleteTxn(id);
        RefreshData();
    }

    public void RefreshData()
    {
        grid.SuspendLayout();
        long? keepId = grid.SelectedRows.Count > 0 && grid.SelectedRows[0].Tag is long k ? k : null;
        grid.Rows.Clear();
        var txns = Db.ListTxns(showDeleted);

        if (showDeleted)
        {
            foreach (var t in txns)
            {
                var rowIdx = grid.Rows.Add(t.Date, t.UnitLabel, KindLabel(t.Kind), t.Category,
                    Theme.Money(t.Amount), t.Note);
                grid.Rows[rowIdx].Tag = t.Id;
                if (keepId.HasValue && t.Id == keepId.Value) grid.Rows[rowIdx].Selected = true;
            }
            grid.ResumeLayout();
            addRent.Visible = addLate.Visible = addHold.Visible = addExp.Visible = delBtn.Visible = false;
            undoBtn.Visible = purgeBtn.Visible = true;
            summary.Text = $"Showing {txns.Count} deleted entry(ies). Select one and use Undo delete to bring it back, or Delete forever to remove it for good.";
            return;
        }

        addRent.Visible = addLate.Visible = addHold.Visible = addExp.Visible = delBtn.Visible = true;
        undoBtn.Visible = purgeBtn.Visible = false;

        var now = DateTime.Today;
        string monthKey = now.ToString("yyyy-MM");
        string yearKey = now.ToString("yyyy");

        decimal inM = 0, outM = 0, inY = 0, outY = 0;
        foreach (var t in txns)
        {
            bool moneyIn = t.Kind is "rent" or "late" or "hold";
            if (t.Date.Length >= 7 && t.Date[..7] == monthKey)
            {
                if (moneyIn) inM += t.Amount; else outM += t.Amount;
            }
            if (t.Date.Length >= 4 && t.Date[..4] == yearKey)
            {
                if (moneyIn) inY += t.Amount; else outY += t.Amount;
            }
        }

        summary.Text = $"This month:  in {Theme.Money(inM)}   ·   out {Theme.Money(outM)}   ·   net {Theme.Money(inM - outM)}\n" +
                       $"This year ({yearKey}):  in {Theme.Money(inY)}   ·   out {Theme.Money(outY)}   ·   net {Theme.Money(inY - outY)}";

        foreach (var t in txns)
        {
            var sign = t.Kind is "rent" or "late" or "hold" ? "+" : "−";
            var rowIdx = grid.Rows.Add(t.Date, t.UnitLabel, KindLabel(t.Kind), t.Category,
                sign + Theme.Money(t.Amount), t.Note);
            var row = grid.Rows[rowIdx];
            row.Tag = t.Id;
            if (t.Kind is "rent" or "late" or "hold")
                row.Cells[4].Style.ForeColor = Theme.Good;
            if (keepId.HasValue && t.Id == keepId.Value) row.Selected = true;
        }
        grid.ResumeLayout();
    }

    static string KindLabel(string kind) => kind switch
    {
        "rent" => "Rent",
        "late" => "Late fee",
        "hold" => "Hold fee",
        _ => "Expense",
    };
}

// ---------- Waitlist tab ----------

public class WaitlistTab : UserControl
{
    readonly DataGridView grid = Ui.MakeGrid();
    readonly Label hint = new()
    {
        Dock = DockStyle.Top,
        Height = 30,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(14, 4, 4, 0),
        Tag = "muted",
    };
    readonly Button add, editB, hold, delBtn, delViewBtn, undoBtn, purgeBtn;
    bool showDeleted = false;

    public WaitlistTab()
    {
        add = Ui.Btn("Add to waitlist", 140, (_, _) => Add());
        editB = Ui.Btn("Edit", 80, (_, _) => EditSelected());
        hold = Ui.Btn("Hold fee paid", 130, (_, _) => ToggleHold());
        delBtn = Ui.Btn("Remove", 90, (_, _) => DeleteSelected());
        delViewBtn = Ui.Btn("Deleted", 90, (_, _) => { showDeleted = !showDeleted; RefreshData(); });
        undoBtn = Ui.Btn("Undo delete", 110, (_, _) => UndoSelected());
        purgeBtn = Ui.Btn("Delete forever", 120, (_, _) => PurgeSelected());

        grid.Columns.Add(Ui.Col("Added", 100));
        grid.Columns.Add(Ui.Col("Name", 160));
        grid.Columns.Add(Ui.Col("Phone", 120));
        grid.Columns.Add(Ui.Col("Size wanted", 110));
        grid.Columns.Add(Ui.Col("Hold fee", 90, DataGridViewContentAlignment.MiddleRight));
        grid.Columns.Add(Ui.Col("Notes", 300));

        Controls.Add(grid);
        Controls.Add(Ui.TopBar(add, editB, hold, delBtn, delViewBtn, undoBtn, purgeBtn));
        Controls.Add(hint);

        grid.CellDoubleClick += (_, _) => { if (!showDeleted) EditSelected(); };
    }

    void UndoSelected()
    {
        if (grid.SelectedRows.Count == 0) return;
        if (grid.SelectedRows[0].Tag is not long id) return;
        Db.RestoreWait(id);
        RefreshData();
    }

    void PurgeSelected()
    {
        if (grid.SelectedRows.Count == 0) return;
        if (grid.SelectedRows[0].Tag is not long id) return;
        if (MessageBox.Show("Remove this entry FOREVER? This cannot be undone.", "Delete forever",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        Db.DeleteWait(id);
        RefreshData();
    }

    void Add()
    {
        using var dlg = new WaitDialog(null);
        if (dlg.ShowDialog(FindForm()) == DialogResult.OK && dlg.W != null)
        {
            Db.SaveWait(dlg.W);
            RefreshData();
        }
    }

    void EditSelected()
    {
        if (grid.SelectedRows.Count == 0) return;
        if (grid.SelectedRows[0].Tag is long id)
        {
            var w = Db.ListWait().FirstOrDefault(x => x.Id == id);
            if (w != null)
            {
                using var dlg = new WaitDialog(w);
                if (dlg.ShowDialog(FindForm()) == DialogResult.OK)
                {
                    Db.SaveWait(dlg.W);
                    RefreshData();
                }
            }
        }
    }

    void ToggleHold()
    {
        if (grid.SelectedRows.Count == 0) return;
        if (grid.SelectedRows[0].Tag is long id)
        {
            var w = Db.ListWait().FirstOrDefault(x => x.Id == id);
            if (w != null)
            {
                Db.SetWaitHoldPaid(id, !w.HoldPaid);
                RefreshData();
            }
        }
    }

    void DeleteSelected()
    {
        if (grid.SelectedRows.Count == 0) return;
        if (grid.SelectedRows[0].Tag is not long id) return;
        if (MessageBox.Show("Remove this entry from the waitlist?\n\nIt moves to the Deleted view (Deleted button) where you can undo it.", "Remove entry",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        Db.SoftDeleteWait(id);
        RefreshData();
    }

    public void RefreshData()
    {
        grid.SuspendLayout();
        long? keepId = grid.SelectedRows.Count > 0 && grid.SelectedRows[0].Tag is long k ? k : null;
        grid.Rows.Clear();
        int waiting = 0, holds = 0;
        foreach (var w in Db.ListWait(showDeleted))
        {
            waiting++;
            if (w.HoldPaid) holds++;
            var rowIdx = grid.Rows.Add(w.Created, w.Name, w.Phone, w.SizeWanted,
                w.HoldPaid ? "Paid ✓" : "", w.Notes);
            var row = grid.Rows[rowIdx];
            row.Tag = w.Id;
            if (w.HoldPaid)
            {
                row.Cells[4].Style.ForeColor = Theme.Good;
                row.Cells[4].Style.BackColor = Theme.GoodBg;
                row.Cells[4].Style.SelectionBackColor = Theme.Accent;
            }
            if (keepId.HasValue && w.Id == keepId.Value) row.Selected = true;
        }
        grid.ResumeLayout();

        add.Visible = editB.Visible = hold.Visible = delBtn.Visible = !showDeleted;
        undoBtn.Visible = purgeBtn.Visible = showDeleted;
        if (showDeleted)
        {
            hint.Text = $"Showing {waiting} deleted waitlist entry(ies). Undo delete brings it back; Delete forever is permanent.";
            return;
        }
        hint.Text = Billing.HoldFeeOn
            ? $"{waiting} waiting · {holds} hold fee(s) collected. Hold fee is set to {Theme.Money(Billing.HoldFeeAmt)} in Settings."
            : $"{waiting} waiting. Hold fees are off — turn them on in Settings if you take deposits.";
    }
}

// ---------- Settings tab ----------

public class SettingsTab : UserControl
{
    public event Action? SettingsChanged;

    readonly RadioButton modeFirst = new() { Text = "Bill on the 1st of the month", AutoSize = true };
    readonly CheckBox prorateChk = new()
    {
        Text = "Prorate the first month for mid-month move-ins",
        AutoSize = true,
        Margin = new Padding(20, 0, 0, 0),
    };
    readonly RadioButton modeAnniv = new() { Text = "Bill on each tenant's move-in day (anniversary)", AutoSize = true };
    readonly TextBox graceT = new() { Width = 80 };
    readonly TextBox lateT = new() { Width = 120 };
    readonly CheckBox holdOn = new() { Text = "Collect hold fees on the waitlist", AutoSize = true };
    readonly TextBox holdAmtT = new() { Width = 120 };

    readonly DataGridView sizeGrid = new()
    {
        Dock = DockStyle.Fill,
        Height = 160,
        AllowUserToAddRows = true,
        AllowUserToDeleteRows = true,
        RowHeadersVisible = false,
        AutoGenerateColumns = false,
        SelectionMode = DataGridViewSelectionMode.CellSelect,
    };

    public SettingsTab()
    {
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(16),
        };

        flow.Add(Head("Billing"));
        flow.Add(modeFirst);
        flow.Add(prorateChk);
        flow.Add(new Label
        {
            Text = "With prorating on, a mid-month move-in owes rent × days left in the move-in month " +
                   "(move-in day included), rounded to the cent, for the first month only.",
            Tag = "muted",
            AutoSize = true,
            MaximumSize = new Size(640, 0),
            ForeColor = Theme.Muted,
            Margin = new Padding(36, 0, 0, 0),
        });
        flow.Add(modeAnniv);
        modeFirst.CheckedChanged += (_, _) => prorateChk.Enabled = modeFirst.Checked;
        flow.Add(Row("Grace period before the late fee (days):", graceT));
        flow.Add(Row("Late fee amount $:", lateT));
        flow.Add(Spacer());

        flow.Add(Head("Hold fees"));
        flow.Add(holdOn);
        flow.Add(Row("Hold fee amount $:", holdAmtT));
        flow.Add(Spacer());

        flow.Add(Head("Unit sizes — what you have and how many"));
        var sizeWrap = new Panel { Height = 160, Width = 640, Padding = new Padding(0, 4, 0, 4) };
        sizeGrid.Columns.Add(Ui.Col("Size (e.g. 10x10)", 160));
        sizeGrid.Columns.Add(Ui.Col("How many", 110, DataGridViewContentAlignment.MiddleRight));
        sizeGrid.Columns.Add(Ui.Col("Rent $/mo", 110, DataGridViewContentAlignment.MiddleRight));
        sizeWrap.Controls.Add(sizeGrid);
        flow.Add(sizeWrap);
        flow.Add(Row("", MakeButton("Add row", 100, (_, _) => sizeGrid.Rows.Add())));
        flow.Add(Row("", MakeButton("Remove selected row", 160, (_, _) =>
        {
            if (sizeGrid.CurrentRow != null && !sizeGrid.CurrentRow.IsNewRow)
                sizeGrid.Rows.RemoveAt(sizeGrid.CurrentRow.Index);
        })));
        flow.Add(Row("", MakeButton("Generate units from this list", 220, (_, _) => GenerateUnits())));
        flow.Add(new Label
        {
            Text = "Generating creates units named 10x10-01, 10x10-02 … and fills their rent from the list. " +
                   "Units that already exist are left alone, so it's safe to run again.",
            Tag = "muted",
            AutoSize = true,
            MaximumSize = new Size(640, 0),
            ForeColor = Theme.Muted,
        });
        flow.Add(Spacer());

        flow.Add(MakeButton("Save settings", 180, (_, _) => Save()));

        Controls.Add(flow);
        LoadSettings();
    }

    static Button MakeButton(string text, int width, EventHandler onClick) => Ui.Btn(text, width, onClick);

    static Label Head(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Tag = "head",
        Font = Theme.SubHeadFont,
        ForeColor = Theme.Accent,
        Margin = new Padding(0, 8, 0, 8),
    };

    static Label Spacer() => new() { Height = 10, Width = 10, Margin = new Padding(0) };

    static FlowLayoutPanel Row(string label, Control c)
    {
        var p = new FlowLayoutPanel
        {
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 2),
        };
        if (label.Length > 0)
            p.Controls.Add(new Label
            {
                Text = label,
                AutoSize = true,
                Margin = new Padding(0, 8, 8, 0),
            });
        c.Margin = new Padding(0, 4, 0, 0);
        p.Controls.Add(c);
        return p;
    }

    void LoadSettings()
    {
        (Billing.Mode == "first" ? modeFirst : modeAnniv).Checked = true;
        prorateChk.Enabled = modeFirst.Checked;
        prorateChk.Checked = Billing.ProrateOn;
        graceT.Text = Billing.GraceDays.ToString();
        lateT.Text = Billing.LateFee == 0 ? "" : Billing.LateFee.ToString("0.##");
        holdOn.Checked = Billing.HoldFeeOn;
        holdAmtT.Text = Billing.HoldFeeAmt == 0 ? "" : Billing.HoldFeeAmt.ToString("0.##");

        sizeGrid.Rows.Clear();
        foreach (var s in Db.GetSizes())
            sizeGrid.Rows.Add(s.Name, s.Qty, s.Price == 0 ? "" : s.Price.ToString("0.##"));
    }

    void Save()
    {
        if (!int.TryParse(graceT.Text.Trim(), out var grace) || grace < 0)
        {
            MessageBox.Show("Grace period must be a number of days (0 or more).", "StoragePaw",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        decimal late = 0;
        if (lateT.Text.Trim().Length > 0 && !Ui.ParseMoney(lateT.Text, out late))
        {
            MessageBox.Show("Late fee should be an amount like 25 or 25.00.", "StoragePaw",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        decimal holdAmt = 0;
        if (holdAmtT.Text.Trim().Length > 0 && !Ui.ParseMoney(holdAmtT.Text, out holdAmt))
        {
            MessageBox.Show("Hold fee should be an amount like 50 or 50.00.", "StoragePaw",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Db.SetSetting("billing_mode", modeFirst.Checked ? "first" : "anniversary");
        Db.SetSetting("prorate_on", modeFirst.Checked && prorateChk.Checked ? "1" : "0");
        Db.SetSetting("grace_days", grace.ToString());
        Db.SetSetting("late_fee", late.ToString(CultureInfo.InvariantCulture));
        Db.SetSetting("hold_fee_on", holdOn.Checked ? "1" : "0");
        Db.SetSetting("hold_fee_amt", holdAmt.ToString(CultureInfo.InvariantCulture));

        var sizes = new List<SizeRow>();
        foreach (DataGridViewRow r in sizeGrid.Rows)
        {
            if (r.IsNewRow) continue;
            string name = CellStr(r, 0);
            if (name.Length == 0) continue;
            int qty = int.TryParse(CellStr(r, 1), out var q) && q > 0 ? q : 0;
            decimal price = 0;
            if (CellStr(r, 2).Length > 0 && !Ui.ParseMoney(CellStr(r, 2), out price)) price = 0;
            if (qty == 0) continue;
            sizes.Add(new SizeRow { Name = name, Qty = qty, Price = price });
        }
        Db.SaveSizes(sizes);

        SettingsChanged?.Invoke();
        MessageBox.Show("Settings saved.", "StoragePaw", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void GenerateUnits()
    {
        Save(); // sizes must be persisted before generating
        var sizes = Db.GetSizes().Where(s => s.Qty > 0).ToList();
        if (sizes.Count == 0)
        {
            MessageBox.Show("Fill in at least one size with a quantity.", "Generate units",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        int total = sizes.Sum(s => s.Qty);
        if (MessageBox.Show(
                $"Create up to {total} unit(s) from this list?\n\nExisting units with the same name are left alone.",
                "Generate units", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        var existing = Db.ListUnits().Select(u => u.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int created = 0;
        foreach (var s in sizes)
        {
            string stem = s.Name.Trim().Replace(' ', '-');
            if (stem.Length == 0) stem = "unit";
            for (int i = 1; i <= s.Qty; i++)
            {
                string name = $"{stem}-{i:D2}";
                if (existing.Contains(name)) continue;
                Db.SaveUnit(new Unit { Name = name, Size = s.Name.Trim(), Rent = s.Price });
                existing.Add(name);
                created++;
            }
        }
        SettingsChanged?.Invoke();
        MessageBox.Show(created > 0
                ? $"Created {created} unit(s)."
                : "Nothing to create — all those unit names already exist.",
            "Generate units", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    static string CellStr(DataGridViewRow r, int i) =>
        r.Cells[i].Value?.ToString()?.Trim() ?? "";
}
