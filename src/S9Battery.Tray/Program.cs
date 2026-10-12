namespace S9Battery.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\S9Battery.Tray", out bool first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "S9 电量工具");
        using var context = new TrayContext();
        Application.Run(context);
    }
}
