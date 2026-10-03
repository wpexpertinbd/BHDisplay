// BHDisplay for Windows — entry point. One instance per user session.
// A tray app has no window, so every failure must be shown and logged — never exit silently.
using System.Runtime.InteropServices;

// Load Windows DLLs (dxva2, crypt32, user32…) only from System32 — never from the folder the exe sits in.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace BHDisplay.Win;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Log.Write($"start {Application.ProductVersion} on {Environment.OSVersion} from {Application.ExecutablePath}");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Fatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Fatal(e.ExceptionObject as Exception);

        SetDpiAwareness();
        Application.EnableVisualStyles();
        if (args.Contains("--uninstall")) { Installer.Uninstall(); return; }
        // Started from a download: install (or update) into the user's Programs folder and run from there.
        if (!Installer.IsInstalledCopy && !args.Contains("--portable")) { Installer.Install(); return; }

        using var single = new Mutex(true, Installer.MutexName, out bool first);
        if (!first)
        {
            MessageBox.Show("BHDisplay is already running.\n\nLook for its icon in the system tray — click the ^ arrow next to the clock if you don't see it.",
                "BHDisplay", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        // (Per-monitor DPI awareness set above: hook coordinates and SetCursorPos are real pixels on every monitor.)
        Application.SetCompatibleTextRenderingDefault(false);
        try { Application.Run(new TrayApp()); }
        catch (Exception e) { Fatal(e); }
        Log.Write("exit");
    }

    /// Per-monitor DPI awareness: hook coordinates and SetCursorPos are then real pixels on every monitor.
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(nint value);
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    private static void SetDpiAwareness()
    {
        try { if (SetProcessDpiAwarenessContext(-4)) return; } catch (EntryPointNotFoundException) { }   // PER_MONITOR_AWARE_V2
        SetProcessDPIAware();
    }

    private static void Fatal(Exception? e)
    {
        Log.Write("FATAL " + e);
        try { TrayApp.EmergencyRelease(); } catch { }
        MessageBox.Show($"BHDisplay hit an error and has to close:\n\n{e?.Message}\n\nDetails were saved to:\n{Log.FilePath}",
            "BHDisplay", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Environment.Exit(1);
    }
}

/// Small rolling log in %LOCALAPPDATA%\BHDisplay (restarted once it passes 256 KB).
internal static class Log
{
    public static string FilePath => Path.Combine(Settings.Dir, "bhdisplay.log");
    private static readonly object Gate = new();
    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Settings.Dir);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 256 * 1024) File.Delete(FilePath);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
