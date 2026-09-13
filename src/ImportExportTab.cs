using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;

namespace StorageCat;

public static class FlowExt
{
    public static void Add(this FlowLayoutPanel p, Control c) => p.Controls.Add(c);
}

public class ImportExportTab : UserControl
{
    public event Action? DataChanged;

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        IncludeFields = true,
        WriteIndented = true,
    };

    class Backup
    {
        public string exported = "";
        public List<UnitRow> units = new();
        public List<TxnRow> transactions = new();
        public List<WaitRow> waitlist = new();
        public List<SizeRow> sizes = new();
    }

    class UnitRow
    {
        public long id;
        public string name = "", size = "", tenant = "", phone = "", move_in = "", notes = "";
        public decimal rent;
    }

    class TxnRow
    {
        public string date = "", kind = "expense", category = "", note = "";
        public long? unit_id;
        public decimal amount;
    }

    class WaitRow
    {
        public string created = "", name = "", phone = "", size_wanted = "", notes = "";
        public bool hold_paid;
    }

    readonly Label dbLabel = new()
    {
        AutoSize = true,
        Tag = "muted",
        Margin = new Padding(0, 2, 0, 8),
    };

    public ImportExportTab()
    {
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(16),
        };

        flow.Add(Head("Rent roll — the sheet your bank, accountant, or lien lawyer asks for"));
        flow.Add(Btn("Export rent roll CSV", 260, (_, _) => ExportRentRoll()));
        flow.Add(Spacer());

        flow.Add(Head("CSV — bring your old spreadsheets in, take your data out any time"));
        flow.Add(Btn("Import transactions CSV", 260, (_, _) => ImportCsv("transactions")));
        flow.Add(Btn("Import units CSV", 260, (_, _) => ImportCsv("units")));
        flow.Add(Btn("Export transactions CSV", 260, (_, _) => ExportCsv("transactions")));
        flow.Add(Btn("Export units CSV", 260, (_, _) => ExportCsv("units")));
        flow.Add(Btn("Export waitlist CSV", 260, (_, _) => ExportCsv("waitlist")));
        flow.Add(Spacer());

        flow.Add(Head("Full backup (JSON) — everything in one file"));
        flow.Add(Btn("Export full backup (JSON)", 260, (_, _) => ExportJson()));
        flow.Add(Btn("Restore from backup (JSON)", 260, (_, _) => ImportJson()));
        flow.Add(Spacer());

        flow.Add(Head("Your data file"));
        dbLabel.Text = "Data lives in one SQLite file you can copy and back up like a photo:\n" + Db.DbPath;
        flow.Add(dbLabel);
        flow.Add(Btn("Open data folder", 260, (_, _) =>
        {
            try
            {
                // UseShellExecute=true: without it the shell verb fails on .NET Core (found by Dad on LedgerCat 1.2.0, fixed there in 1.2.1)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + Db.DbPath + "\"",
                    UseShellExecute = true,
                });
            }
            catch { MessageBox.Show("Data folder: " + Db.DataDir, "StorageCat"); }
        }));

        Controls.Add(flow);
    }

    static Label Head(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Tag = "head",
        Font = Theme.SubHeadFont,
        ForeColor = Theme.Accent,
        Margin = new Padding(0, 8, 0, 8),
        MaximumSize = new Size(760, 0),
    };

    static Button Btn(string text, int width, EventHandler onClick)
    {
        var b = new Button
        {
            Text = text,
            Width = width,
            Height = 34,
            Margin = new Padding(0, 2, 0, 2),
        };
        // wire it! object initializers can't assign events — this helper used to take
        // the handler and drop it on the floor, leaving every button on this tab dead
        // (same bug LedgerCat had until v1.2.4; Dad hit it again here, v0.1.1 test)
        if (onClick != null) b.Click += onClick;
        return b;
    }

    static Label Spacer() => new() { Height = 10, Width = 10, Margin = new Padding(0) };

    // ---------- rent roll ----------

    void ExportRentRoll()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "Rent roll CSV|*.csv",
            FileName = $"rent-roll-{DateTime.Today:yyyy-MM-dd}.csv",
            Title = "Export rent roll",
        };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;

        var txns = Db.ListTxns();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("unit,size,tenant,phone,rent_per_month,move_in,this_month,last_month,days_late,aging_bucket");
        foreach (var u in Db.ListUnits())
        {
            var (status, daysLate) = Billing.Status(u, txns, today);
            sb.AppendLine(Csv.Row(
                u.Name, u.Size, u.Tenant, u.Phone,
                u.Rent.ToString("0.##", CultureInfo.InvariantCulture),
                u.MoveIn,
                status.Length > 0 ? status : "vacant",
                Billing.PrevMonthStatus(u, txns, today).Length > 0 ? Billing.PrevMonthStatus(u, txns, today) : "vacant",
                daysLate > 0 ? daysLate.ToString() : "0",
                daysLate > 0 ? Billing.Bucket(daysLate) : ""));
        }
        File.WriteAllText(dlg.FileName, sb.ToString());
        MessageBox.Show("Rent roll exported to:\n" + dlg.FileName, "Export complete",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ---------- CSV ----------

    static readonly Dictionary<string, (string filter, string filePrefix)> CsvKinds = new()
    {
        ["transactions"] = ("Transactions CSV|*.csv", "transactions"),
        ["units"] = ("Units CSV|*.csv", "units"),
        ["waitlist"] = ("Waitlist CSV|*.csv", "waitlist"),
    };

    void ImportCsv(string kindKey)
    {
        using var dlg = new OpenFileDialog { Filter = CsvKinds[kindKey].filter, Title = "Import " + kindKey };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;

        try
        {
            var rows = Csv.Parse(File.ReadAllText(dlg.FileName));
            if (rows.Count < 2)
            {
                MessageBox.Show("That file has a header but no data rows.", "Import",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var header = rows[0];
            int ok = 0, skipped = 0;
            var units = Db.ListUnits();

            foreach (var r in rows.Skip(1))
            {
                bool added = kindKey switch
                {
                    "transactions" => ImportTxnRow(header, r, ref units),
                    "units" => ImportUnitRow(header, r, ref units),
                    _ => false,
                };
                if (added) ok++; else skipped++;
            }

            DataChanged?.Invoke();
            MessageBox.Show($"Imported {ok} row(s)" + (skipped > 0 ? $", skipped {skipped} row(s) with missing or bad values." : "."),
                "Import complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not import that file:\n" + ex.Message, "Import failed",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    long? ResolveUnit(string raw, ref List<Unit> units)
    {
        raw = raw.Trim();
        if (raw.Length == 0) return null;
        var u = units.FirstOrDefault(x => x.Name.Equals(raw, StringComparison.OrdinalIgnoreCase));
        if (u != null) return u.Id;

        var nu = new Unit { Name = raw };
        nu.Id = Db.SaveUnit(nu);
        units.Add(nu);
        return nu.Id;
    }

    static decimal? ParseAmountCell(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim().Replace("$", "").Replace(",", "").TrimStart('+', '−', '-');
        if (decimal.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0)
            return v;
        return null;
    }

    bool ImportTxnRow(string[] header, string[] r, ref List<Unit> units)
    {
        int cDate = Csv.FindCol(header, "date");
        int cUnit = Csv.FindCol(header, "unit");
        int cKind = Csv.FindCol(header, "kind");
        int cCat = Csv.FindCol(header, "category");
        int cAmt = Csv.FindCol(header, "amount");
        int cNote = Csv.FindCol(header, "note");
        if (cDate < 0 || cAmt < 0) return false;

        string date = Cell(r, cDate).Trim();
        if (!Ui.ParseDate(date, out var d)) return false;
        var amountOpt = ParseAmountCell(Cell(r, cAmt));
        if (amountOpt is not { } amount) return false;

        string kindRaw = Cell(r, cKind).Trim().ToLowerInvariant();
        string kind = kindRaw switch
        {
            var k when k.StartsWith("rent") || k.StartsWith("in") => "rent",
            var k when k.StartsWith("late") => "late",
            var k when k.StartsWith("hold") => "hold",
            _ => "expense",
        };

        Db.SaveTxn(new Txn
        {
            Date = d.ToString("yyyy-MM-dd"),
            UnitId = cUnit >= 0 ? ResolveUnit(Cell(r, cUnit), ref units) : null,
            Kind = kind,
            Category = cCat >= 0 ? Cell(r, cCat).Trim() : "",
            Amount = amount,
            Note = cNote >= 0 ? Cell(r, cNote).Trim() : "",
        });
        return true;
    }

    bool ImportUnitRow(string[] header, string[] r, ref List<Unit> units)
    {
        int cName = Csv.FindCol(header, "name");
        int cSize = Csv.FindCol(header, "size");
        int cTenant = Csv.FindCol(header, "tenant");
        int cPhone = Csv.FindCol(header, "phone");
        int cRent = Csv.FindCol(header, "rent");
        int cMoveIn = Csv.FindCol(header, "move_in");
        int cNotes = Csv.FindCol(header, "notes");
        if (cName < 0) return false;

        string name = Cell(r, cName).Trim();
        if (name.Length == 0) return false;
        if (units.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return false; // already exists

        var rent = cRent >= 0 ? ParseAmountCell(Cell(r, cRent)) ?? 0m : 0m;
        string moveIn = cMoveIn >= 0 ? Cell(r, cMoveIn).Trim() : "";
        if (moveIn.Length > 0 && !Ui.ParseDate(moveIn, out _)) moveIn = "";

        var nu = new Unit
        {
            Name = name,
            Size = cSize >= 0 ? Cell(r, cSize).Trim() : "",
            Tenant = cTenant >= 0 ? Cell(r, cTenant).Trim() : "",
            Phone = cPhone >= 0 ? Cell(r, cPhone).Trim() : "",
            Rent = rent,
            MoveIn = moveIn,
            Notes = cNotes >= 0 ? Cell(r, cNotes).Trim() : "",
        };
        nu.Id = Db.SaveUnit(nu);
        units.Add(nu);
        return true;
    }

    static string Cell(string[] r, int i) => i >= 0 && i < r.Length ? r[i] : "";

    void ExportCsv(string kindKey)
    {
        var (filter, prefix) = CsvKinds[kindKey];
        using var dlg = new SaveFileDialog
        {
            Filter = filter,
            FileName = $"{prefix}-{DateTime.Today:yyyy-MM-dd}.csv",
            Title = "Export " + kindKey,
        };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;

        var sb = new System.Text.StringBuilder();

        switch (kindKey)
        {
            case "transactions":
                sb.AppendLine("date,unit,kind,category,amount,note");
                foreach (var t in Db.ListTxns())
                    sb.AppendLine(Csv.Row(t.Date, t.UnitLabel, t.Kind, t.Category,
                        t.Amount.ToString("0.##", CultureInfo.InvariantCulture), t.Note));
                break;
            case "units":
                sb.AppendLine("name,size,tenant,phone,rent,move_in,notes");
                foreach (var u in Db.ListUnits())
                    sb.AppendLine(Csv.Row(u.Name, u.Size, u.Tenant, u.Phone,
                        u.Rent.ToString("0.##", CultureInfo.InvariantCulture), u.MoveIn, u.Notes));
                break;
            case "waitlist":
                sb.AppendLine("created,name,phone,size_wanted,hold_fee_paid,notes");
                foreach (var w in Db.ListWait())
                    sb.AppendLine(Csv.Row(w.Created, w.Name, w.Phone, w.SizeWanted,
                        w.HoldPaid ? "yes" : "no", w.Notes));
                break;
        }

        File.WriteAllText(dlg.FileName, sb.ToString());
        MessageBox.Show("Exported to:\n" + dlg.FileName, "Export complete",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ---------- JSON backup ----------

    void ExportJson()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "StorageCat backup (JSON)|*.json",
            FileName = $"storagecat-backup-{DateTime.Today:yyyy-MM-dd}.json",
        };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;

        var backup = new Backup { exported = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
        foreach (var u in Db.ListUnits())
            backup.units.Add(new UnitRow
            {
                id = u.Id,
                name = u.Name, size = u.Size, tenant = u.Tenant, phone = u.Phone,
                rent = u.Rent, move_in = u.MoveIn, notes = u.Notes,
            });
        foreach (var t in Db.ListTxns())
            backup.transactions.Add(new TxnRow
            {
                date = t.Date, unit_id = t.UnitId, kind = t.Kind,
                category = t.Category, amount = t.Amount, note = t.Note,
            });
        foreach (var w in Db.ListWait())
            backup.waitlist.Add(new WaitRow
            {
                created = w.Created, name = w.Name, phone = w.Phone,
                size_wanted = w.SizeWanted, hold_paid = w.HoldPaid, notes = w.Notes,
            });
        backup.sizes = Db.GetSizes();

        File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(backup, JsonOpts));
        MessageBox.Show("Backup saved to:\n" + dlg.FileName, "Backup complete",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void ImportJson()
    {
        using var dlg = new OpenFileDialog { Filter = "StorageCat backup (JSON)|*.json" };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;

        try
        {
            var backup = JsonSerializer.Deserialize<Backup>(File.ReadAllText(dlg.FileName), JsonOpts);
            if (backup == null)
            {
                MessageBox.Show("That file doesn't look like a StorageCat backup.", "Restore",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (MessageBox.Show(
                    "Restoring a backup REPLACES everything currently in StorageCat.\nContinue?",
                    "Restore backup", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            Db.WipeAll();
            var idMap = new Dictionary<long, long>();
            foreach (var u in backup.units)
            {
                var nu = new Unit
                {
                    Name = u.name, Size = u.size, Tenant = u.tenant, Phone = u.phone,
                    Rent = u.rent, MoveIn = u.move_in, Notes = u.notes,
                };
                nu.Id = Db.SaveUnit(nu);
                idMap[u.id] = nu.Id;
            }
            foreach (var t in backup.transactions)
            {
                long? uid = null;
                if (t.unit_id.HasValue && idMap.TryGetValue(t.unit_id.Value, out var m1)) uid = m1;
                Db.SaveTxn(new Txn
                {
                    Date = t.date, UnitId = uid, Kind = t.kind,
                    Category = t.category, Amount = t.amount, Note = t.note,
                });
            }
            foreach (var w in backup.waitlist)
            {
                Db.SaveWait(new WaitEntry
                {
                    Created = w.created, Name = w.name, Phone = w.phone,
                    SizeWanted = w.size_wanted, HoldPaid = w.hold_paid, Notes = w.notes,
                });
            }
            if (backup.sizes.Count > 0) Db.SaveSizes(backup.sizes);

            DataChanged?.Invoke();
            MessageBox.Show($"Restored {backup.units.Count} unit(s), {backup.transactions.Count} transaction(s), {backup.waitlist.Count} waitlist row(s).",
                "Restore complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not restore that backup:\n" + ex.Message, "Restore failed",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
