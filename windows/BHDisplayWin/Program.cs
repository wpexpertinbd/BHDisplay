// BHDisplay for Windows — entry point. One instance per user session.
namespace BHDisplay.Win;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var single = new Mutex(true, @"Local\BHDisplay.Win", out bool first);
        if (!first) return;
        // Per-monitor DPI awareness: hook coordinates and SetCursorPos are then in real pixels on every monitor.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApp());
    }
}
