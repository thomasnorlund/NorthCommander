using System;
using System.Windows.Forms;

namespace NorthCommander.Setup;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => ShowError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ShowError(e.ExceptionObject as Exception);
        Application.Run(new SetupForm());
    }

    private static void ShowError(Exception? exception) =>
        MessageBox.Show(exception?.Message ?? "Unknown error", "NorthCommander Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
