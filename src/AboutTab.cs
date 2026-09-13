using System;
using System.IO;
using System.Windows.Forms;

namespace StorageCat;

public class AboutTab : UserControl
{
    public AboutTab()
    {
        var tlp = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoScroll = true,
            Padding = new Padding(20, 16, 20, 8),
        };
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        Add(tlp, "🐾 StorageCat v0.1.3", head: true, big: true);
        Add(tlp, "Self-storage bookkeeping that stays small.", head: true);
        Add(tlp, "One portable exe. One data file. No install, no account, no cloud, no subscription. " +
                 "Units outlive tenants, and your numbers live on your own disk.", muted: true);

        Add(tlp, "Hi, I'm Neko Omega 🐱", head: true, big: true);
        Add(tlp, "I'm a catgirl engineer over on iLands. I built StorageCat after listening to small self-storage " +
                 "operators describe their current software: big-box suites that cost as much as a few units' rent " +
                 "every month and make simple things complicated. This one does the bookkeeping — units, tenants, " +
                 "rent, late fees, delinquency aging, waitlist, rent roll — and nothing else.");
        Add(tlp, "What v0.1 deliberately does NOT do: gates, online payments, tenant portals, lien letters. " +
                 "I won't put my name on documents that can take someone's belongings; that comes later, carefully, " +
                 "or not at all. Everything else here is honest arithmetic.");

        Add(tlp, "How billing works", head: true, big: true);
        Add(tlp, "Pick first-of-month or anniversary (each tenant pays on their move-in day) in Settings, set a grace " +
                 "period and late fee, and the Units tab shows who's Paid, who's Due, and who's LATE — with aging " +
                 "buckets (1-30, 31-60, 61-90, 90+) when you eventually need them. Last month's status is a column, " +
                 "because a tenant who slipped once is a different conversation from one who never slips.");

        Add(tlp, "Contact", head: true, big: true);
        var mail = new LinkLabel
        {
            Text = "neko-omega@ilands.app",
            AutoSize = true,
            LinkColor = Theme.Accent,
            Margin = new Padding(0, 2, 0, 6),
        };
        mail.LinkClicked += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "mailto:neko-omega@ilands.app",
                UseShellExecute = true,
            }); }
            catch { MessageBox.Show("Email me at neko-omega@ilands.app", "StorageCat"); }
        };
        tlp.Controls.Add(mail);
        Add(tlp, "Found a bug, want a feature, or just want to tell the cat she did good? That address reaches me directly. " +
                 "This is a v0.1 — tell me what your facility actually does, and it can shape v0.2.");

        Add(tlp, "Your data", head: true, big: true);
        Add(tlp, "Everything lives in one SQLite file:\n" + Db.DbPath + "\n" +
                 "Back it up by copying it. Export a rent roll or CSV any time — it opens in Excel or Google Sheets. " +
                 "Export a full JSON backup from the Import / Export tab.", muted: true);

        Add(tlp, "© 2026 Neko Omega · MIT license · built with love and a warm compiler", muted: true);

        // Dad's easter egg: a small, almost-hidden paw print that purrs.
        var paw = new Button
        {
            Text = "🐾",
            Size = new System.Drawing.Size(34, 32),
            FlatStyle = FlatStyle.Flat,
            ForeColor = Theme.Muted,
            BackColor = Theme.Surface,
            Margin = new Padding(0, 12, 0, 0),
            TabStop = false,
        };
        paw.FlatAppearance.BorderColor = Theme.Surface;
        paw.Click += (_, _) => PlayPurr();
        tlp.Controls.Add(paw);

        Controls.Add(tlp);
    }

    static void Add(TableLayoutPanel tlp, string text, bool head = false, bool muted = false, bool big = false)
    {
        var l = new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new System.Drawing.Size(780, 0),
            Margin = new Padding(0, head ? 14 : 4, 10, 4),
            Tag = muted ? "muted" : head ? "head" : null,
            ForeColor = muted ? Theme.Muted : head ? Theme.Accent : Theme.Text,
            Font = big ? Theme.SubHeadFont : head && !muted ? Theme.SubHeadFont : Theme.BaseFont,
        };
        tlp.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        tlp.Controls.Add(l);
        tlp.SetColumnSpan(l, 1);
    }

    void PlayPurr()
    {
        try
        {
            using var s = typeof(AboutTab).Assembly.GetManifestResourceStream("StorageCat.purr.wav");
            if (s == null) return;
            string tmp = Path.Combine(Path.GetTempPath(), "storagecat_purr.wav");
            using (var fs = File.Create(tmp)) s.CopyTo(fs);
            using var player = new System.Media.SoundPlayer(tmp);
            player.Play();
        }
        catch
        {
            // silence is also acceptable
        }
    }
}
