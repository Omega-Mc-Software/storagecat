using System;
using System.Windows.Forms;

namespace StorageCat;

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Db.EnsureCreated();
        Theme.LoadSaved();
        Application.Run(new MainForm());
    }
}
