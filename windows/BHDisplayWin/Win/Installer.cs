// Per-user install without admin rights: the downloaded BHDisplay.exe copies itself to
// %LOCALAPPDATA%\Programs\BHDisplay, adds a Start menu shortcut, starts with Windows, and appears in
// Settings ▸ Apps (where "Uninstall" removes everything again). Running a newer download updates in place.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32;

namespace BHDisplay.Win;

internal static class Installer
{
    public const string MutexName = @"Local\BHDisplay.Win";
    public const string QuitEventName = @"Local\BHDisplay.Win.Quit";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\BHDisplay";

    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "BHDisplay");
    public static string InstalledExe => Path.Combine(InstallDir, "BHDisplay.exe");
    private static string Shortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "BHDisplay.lnk");

    public static bool IsInstalledCopy =>
        string.Equals(Path.GetFullPath(Environment.ProcessPath ?? ""), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    /// Install or update from the downloaded file, then start the installed copy.
    public static void Install()
    {
        try
        {
            if (!StopRunningCopy()) { Tell("BHDisplay is still running and didn't close. Quit it from the tray icon, then run this file again.", true); return; }
            Directory.CreateDirectory(InstallDir);
            bool firstInstall = Registry.CurrentUser.OpenSubKey(UninstallKey) is null;
            bool autostart;
            using (var r = Registry.CurrentUser.OpenSubKey(RunKey)) autostart = r?.GetValue("BHDisplay") is string;
            CopyWithRetry(Environment.ProcessPath!, InstalledExe);
            // Start with Windows: on for a new install; on an update keep whatever the user chose.
            if (firstInstall || autostart)
                using (var run = Registry.CurrentUser.CreateSubKey(RunKey)) run.SetValue("BHDisplay", $"\"{InstalledExe}\"");
            using (var u = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                u.SetValue("DisplayName", "BHDisplay");
                u.SetValue("DisplayVersion", Application.ProductVersion.Split('+')[0]);
                u.SetValue("Publisher", "BiswasHost");
                u.SetValue("URLInfoAbout", "https://www.biswashost.com/");
                u.SetValue("DisplayIcon", $"\"{InstalledExe}\"");
                u.SetValue("InstallLocation", InstallDir);
                u.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
                u.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
                u.SetValue("NoModify", 1, RegistryValueKind.DWord);
                u.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            CreateShortcut(Shortcut, InstalledExe);
            Log.Write($"installed to {InstallDir}");
            Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true, WorkingDirectory = InstallDir });
            Tell("BHDisplay is installed.\n\n• It starts by itself when Windows starts.\n• Find it in the Start menu as \"BHDisplay\".\n" +
                 "• To remove it: Settings ▸ Apps ▸ BHDisplay ▸ Uninstall.\n\nYou can delete the downloaded file now.", false);
        }
        catch (Exception e)
        {
            Log.Write("install failed: " + e);
            Tell("BHDisplay couldn't be installed:\n\n" + e.Message, true);
        }
    }

    /// Settings ▸ Apps ▸ Uninstall runs the installed copy with --uninstall.
    public static void Uninstall()
    {
        if (MessageBox.Show("Remove BHDisplay from this PC?\n\nThis also forgets the pairing with your Mac.", "BHDisplay",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        StopRunningCopy();
        try { using var run = Registry.CurrentUser.OpenSubKey(RunKey, true); run?.DeleteValue("BHDisplay", false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        try { File.Delete(Shortcut); } catch { }
        Tell("BHDisplay has been removed.", false);
        // This process runs from the folder being removed: delete it a moment after we exit (we exit right after
        // starting this, so the exe is no longer locked). Full System32 paths: nothing is looked up elsewhere.
        var sys = Environment.SystemDirectory;
        var dirs = $"\"{InstallDir}\" \"{Settings.Dir}\"";
        Process.Start(new ProcessStartInfo(Path.Combine(sys, "cmd.exe"), $"/c \"{Path.Combine(sys, "PING.EXE")}\" 127.0.0.1 -n 3 >nul & rmdir /s /q {dirs}")
            { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = sys });
    }

    /// Asks a running BHDisplay to quit and waits until it has. True when none is running any more.
    private static bool StopRunningCopy()
    {
        if (!IsRunning()) return true;
        try { using var quit = EventWaitHandle.OpenExisting(QuitEventName); quit.Set(); } catch { }
        for (int i = 0; i < 50; i++) { if (!IsRunning()) return true; Thread.Sleep(100); }
        return false;
    }

    private static bool IsRunning()
    {
        try { using var m = Mutex.OpenExisting(MutexName); return true; }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static void CopyWithRetry(string from, string to)
    {
        for (int i = 0; ; i++)
        {
            try { File.Copy(from, to, overwrite: true); return; }
            catch (IOException) when (i < 20) { Thread.Sleep(250); }   // the old copy may take a moment to unlock
        }
    }

    private static void Tell(string text, bool warn) =>
        MessageBox.Show(text, "BHDisplay", MessageBoxButtons.OK, warn ? MessageBoxIcon.Warning : MessageBoxIcon.Information);

    // ---- Start menu shortcut through the Shell's own IShellLink ----

    private static void CreateShortcut(string lnk, string target)
    {
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(target);
        link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
        link.SetDescription("BHDisplay — monitor switch and keyboard & mouse sharing");
        link.SetIconLocation(target, 0);
        ((IPersistFile)link).Save(lnk, true);
        Marshal.FinalReleaseComObject(link);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder f, int cch, nint pfd, uint flags);
        void GetIDList(out nint ppidl);
        void SetIDList(nint pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int cmd);
        void SetShowCmd(int cmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder path, int cch, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string rel, uint reserved);
        void Resolve(nint hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
