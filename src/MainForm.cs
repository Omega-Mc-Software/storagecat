using System;
using System.Windows.Forms;

namespace StorageCat;

public class MainForm : Form
{
    readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    readonly UnitsTab unitsTab = new();
    readonly MoneyTab moneyTab = new();
    readonly WaitlistTab waitTab = new();
    readonly SettingsTab settingsTab = new();
    readonly ImportExportTab ioTab = new();
    readonly AboutTab aboutTab = new();
    readonly Button themeBtn = new() { Dock = DockStyle.Right, Width = 150 };
    readonly Label title = new()
    {
        Text = "🐾 StoragePaw",
        Dock = DockStyle.Left,
        Width = 230,
        TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
        Tag = "head",
        Font = Theme.HeadFont,
    };

    public MainForm()
    {
        Text = "StoragePaw v0.1.4 — by Neko Omega";
        Width = 1180;
        Height = 720;
        MinimumSize = new System.Drawing.Size(960, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Theme.BaseFont;

        var top = new Panel { Dock = DockStyle.Top, Height = 60, Padding = new Padding(10, 8, 10, 8) };
        themeBtn.Click += (_, _) => Theme.Toggle();
        top.Controls.Add(themeBtn);
        top.Controls.Add(title);

        AddTab("Units", unitsTab);
        AddTab("Money In / Out", moneyTab);
        AddTab("Waitlist", waitTab);
        AddTab("Settings", settingsTab);
        AddTab("Import / Export", ioTab);
        AddTab("About", aboutTab);

        Controls.Add(tabs);
        Controls.Add(top);

        ioTab.DataChanged += RefreshAll;
        settingsTab.SettingsChanged += RefreshAll;
        Theme.Changed += ApplyTheme;
        tabs.SelectedIndexChanged += (_, _) => RefreshAll(); // data is fresh every time you land on a tab

        Load += (_, _) =>
        {
            RefreshAll();
            ApplyTheme();
        };
    }

    void AddTab(string titleText, UserControl uc)
    {
        uc.Dock = DockStyle.Fill;
        var page = new TabPage(titleText);
        page.Controls.Add(uc);
        tabs.TabPages.Add(page);
    }

    void RefreshAll()
    {
        unitsTab.RefreshData();
        moneyTab.RefreshData();
        waitTab.RefreshData();
    }

    void ApplyTheme()
    {
        Theme.Apply(this);
        themeBtn.Text = Theme.Dark ? "☀  Light mode" : "🌙  Dark mode";
        RefreshAll(); // re-applies data-dependent row colors (late units, paid marks)
    }
}
