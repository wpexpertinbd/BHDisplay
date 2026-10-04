// Turns Windows' output to the shared monitor off while that monitor shows the Mac, so Windows uses only its other
// screen(s) — apps and windows no longer open on a screen you can't see — and back on before switching to this PC.
// Same idea as the Mac app's DisplayPower. Uses the documented ChangeDisplaySettingsEx (detach = position + size 0).
using System.Runtime.InteropServices;

namespace BHDisplay.Win;

internal static class DisplayPower
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsW(string device, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsExW(string device, ref DEVMODE dm, nint hwnd, uint flags, nint param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsExW(string? device, nint dm, nint hwnd, uint flags, nint param);
    private const int ENUM_CURRENT_SETTINGS = -1, ENUM_REGISTRY_SETTINGS = -2;
    private const int DM_POSITION = 0x20, DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;
    private const uint CDS_UPDATEREGISTRY = 0x1, CDS_NORESET = 0x10000000;
    // The modern display-configuration API — what Win+P → Extend uses. Re-attaching through ChangeDisplaySettingsEx is
    // refused on some drivers/Windows builds (seen on Windows 11 build 26300 + AMD), this always works.
    [DllImport("user32.dll")] private static extern int SetDisplayConfig(uint numPaths, nint paths, uint numModes, nint modes, uint flags);
    private const uint SDC_TOPOLOGY_EXTEND = 0x4, SDC_APPLY = 0x80;
    public static string LastError = "";
    /// One display change at a time: a turn-on waits for an in-flight turn-off, then sees the display really off.
    private static readonly object Gate = new();

    private const int ATTACHED_TO_DESKTOP = 0x1, PRIMARY_DEVICE = 0x4;
    private const uint CDS_SET_PRIMARY = 0x10;

    /// Displays that are part of the desktop right now (read fresh from Windows, not a cached screen list).
    private static List<(string Name, bool Primary)> Active()
    {
        var list = new List<(string, bool)>();
        var dd = new Native.DISPLAY_DEVICE { cb = Marshal.SizeOf<Native.DISPLAY_DEVICE>() };
        for (uint i = 0; Native.EnumDisplayDevicesW(null, i, ref dd, 0); i++, dd.cb = Marshal.SizeOf<Native.DISPLAY_DEVICE>())
            if ((dd.StateFlags & ATTACHED_TO_DESKTOP) != 0) list.Add((dd.DeviceName, (dd.StateFlags & PRIMARY_DEVICE) != 0));
        return list;
    }

    /// Number of displays currently part of the desktop.
    public static int ActiveCount() => Active().Count;
    public static bool IsPrimary(string device) => Active().Any(a => a.Name == device && a.Primary);

    /// The largest active display other than `device` — becomes the main one while `device` is off.
    public static string? OtherDisplay(string device)
    {
        string? best = null; long bestArea = -1;
        foreach (var (name, _) in Active().Where(a => a.Name != device))
        {
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettingsW(name, ENUM_CURRENT_SETTINGS, ref dm)) continue;
            long area = (long)dm.dmPelsWidth * dm.dmPelsHeight;
            if (area > bestArea) { bestArea = area; best = name; }
        }
        return best;
    }

    // ---- the display-configuration (CCD) API: the one Windows' own display settings and Win+P use ----
    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct PATH_SOURCE { public LUID Adapter; public uint Id, ModeIdx, Status; }
    [StructLayout(LayoutKind.Sequential)] private struct PATH_TARGET
    { public LUID Adapter; public uint Id, ModeIdx; public int Tech, Rotation, Scaling; public uint RefreshNum, RefreshDen; public int ScanLine, Available; public uint Status; }
    [StructLayout(LayoutKind.Sequential)] private struct PATH_INFO { public PATH_SOURCE Source; public PATH_TARGET Target; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private unsafe struct MODE_INFO
    { public int InfoType; public uint Id; public LUID Adapter; public fixed int Union[12]; }   // source mode: w, h, format, x, y
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct SOURCE_NAME
    { public int Type; public uint Size; public LUID Adapter; public uint Id;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string GdiName; }
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PATH_INFO[] paths, ref uint numModes, [Out] MODE_INFO[] modes, nint topology);
    [DllImport("user32.dll")] private static extern int SetDisplayConfig(uint numPaths, [In] PATH_INFO[] paths, uint numModes, [In] MODE_INFO[] modes, uint flags);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref SOURCE_NAME info);
    private const uint QDC_ONLY_ACTIVE_PATHS = 2, SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20, SDC_SAVE_TO_DATABASE = 0x200, SDC_ALLOW_CHANGES = 0x400;
    private const int MODE_SOURCE = 1, GET_SOURCE_NAME = 1;

    /// Main display = the one whose desktop starts at (0,0): shift every source so `target` lands there.
    private static unsafe int MakePrimaryCcd(string target)
    {
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var np, out var nm) != 0) return -10;
        var paths = new PATH_INFO[np]; var modes = new MODE_INFO[nm];
        int q = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, 0);
        if (q != 0) return q;
        int? tx = null, ty = null;
        for (int i = 0; i < np; i++)
        {
            var n = new SOURCE_NAME { Type = GET_SOURCE_NAME, Size = (uint)Marshal.SizeOf<SOURCE_NAME>(), Adapter = paths[i].Source.Adapter, Id = paths[i].Source.Id };
            if (DisplayConfigGetDeviceInfo(ref n) != 0 || n.GdiName != target) continue;
            uint mi = paths[i].Source.ModeIdx;
            if (mi >= nm || modes[mi].InfoType != MODE_SOURCE) return -11;
            fixed (int* u = modes[mi].Union) { tx = u[3]; ty = u[4]; }
            break;
        }
        if (tx is null || ty is null) return -12;
        int dx = tx.Value, dy = ty.Value;
        if (dx == 0 && dy == 0) return 0;
        for (int i = 0; i < nm; i++)
            if (modes[i].InfoType == MODE_SOURCE)
                fixed (int* u = modes[i].Union) { u[3] -= dx; u[4] -= dy; }
        return SetDisplayConfig(np, paths, nm, modes, SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_SAVE_TO_DATABASE | SDC_ALLOW_CHANGES);
    }

    /// Makes `target` the main display: it moves to (0,0), the others keep their arrangement around it.
    /// Windows never detaches its main display and opens new windows there.
    public static bool MakePrimary(string target)
    {
        lock (Gate)
        {
            if (!IsOn(target)) { LastError = $"{target} is not on"; return false; }
            if (IsPrimary(target)) { LastError = "already the main display"; return true; }
            int ccd;
            try { ccd = MakePrimaryCcd(target); } catch (Exception e) { ccd = -20; LastError = e.Message; }
            Thread.Sleep(200);
            bool ok = ccd == 0 && IsPrimary(target);
            LastError = $"main display {target}: {(ok ? "ok" : $"display config {ccd}")}";
            return ok;    // one all-or-nothing change: never a half-shifted layout
        }
    }

    /// Where `device` sits and its mode — saved before turning it off. Null for the last or the main screen.
    public static (int X, int Y, int W, int H, int Hz)? CurrentLayout(string device)
    {
        var active = Active();
        if (active.Count < 2 || !active.Any(a => a.Name == device)) return null;
        if (active.Any(a => a.Name == device && a.Primary)) return null;      // Windows won't detach the main display
        var cur = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettingsW(device, ENUM_CURRENT_SETTINGS, ref cur)) return null;
        return (cur.dmPositionX, cur.dmPositionY, cur.dmPelsWidth, cur.dmPelsHeight, cur.dmDisplayFrequency);
    }

    /// Detaches `device` (\\.\DISPLAYn) if `stillWanted()` (checked under the lock). Refuses the last or the main screen.
    public static bool TurnOff(string device, Func<bool> stillWanted)
    {
        lock (Gate) return TurnOffLocked(device, stillWanted);
    }

    private static bool TurnOffLocked(string device, Func<bool> stillWanted)
    {
        if (!stillWanted() || CurrentLayout(device) is null) return false;
        var off = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>(), dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT };
        if (ChangeDisplaySettingsExW(device, ref off, 0, CDS_UPDATEREGISTRY | CDS_NORESET, 0) != 0) return false;
        return ChangeDisplaySettingsExW(null, 0, 0, 0, 0) == 0;
    }

    /// The display output still exists (it may be renumbered or gone after a driver/GPU change).
    public static bool Exists(string device)
    {
        var dd = new Native.DISPLAY_DEVICE { cb = Marshal.SizeOf<Native.DISPLAY_DEVICE>() };
        for (uint i = 0; Native.EnumDisplayDevicesW(null, i, ref dd, 0); i++, dd.cb = Marshal.SizeOf<Native.DISPLAY_DEVICE>())
            if (dd.DeviceName == device) return true;
        return false;
    }

    public static bool IsOn(string device) => Active().Any(a => a.Name == device);

    /// Re-attaches `device`: first at its saved place and mode, else the way Win+P → Extend does it.
    public static bool TurnOn(string device, int x, int y, int w, int h, int hz)
    {
        lock (Gate) return TurnOnLocked(device, x, y, w, h, hz);
    }

    private static bool TurnOnLocked(string device, int x, int y, int w, int h, int hz)
    {
        if (IsOn(device)) return true;
        var classic = TurnOnClassic(device, x, y, w, h, hz);
        LastError = $"ChangeDisplaySettingsEx {classic}";
        if (classic == 0) { Thread.Sleep(300); if (IsOn(device)) return true; }
        var ext = SetDisplayConfig(0, 0, 0, 0, SDC_APPLY | SDC_TOPOLOGY_EXTEND);
        LastError = $"ChangeDisplaySettingsEx {classic}, SetDisplayConfig(extend) {ext}";
        return ext == 0;
    }

    private static int TurnOnClassic(string device, int x, int y, int w, int h, int hz)
    {
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        EnumDisplaySettingsW(device, ENUM_REGISTRY_SETTINGS, ref dm);
        dm.dmSize = (short)Marshal.SizeOf<DEVMODE>();
        dm.dmPositionX = x; dm.dmPositionY = y;
        dm.dmPelsWidth = w > 0 ? w : 1920; dm.dmPelsHeight = h > 0 ? h : 1080;
        dm.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT;
        // The saved place must not overlap a screen that is on now (the layout may have changed meanwhile).
        var r = new System.Drawing.Rectangle(x, y, dm.dmPelsWidth, dm.dmPelsHeight);
        if (Screen.AllScreens.Any(s => s.Bounds.IntersectsWith(r))) dm.dmFields &= ~DM_POSITION;
        if (hz > 0) { dm.dmDisplayFrequency = hz; dm.dmFields |= DM_DISPLAYFREQUENCY; }
        var r1 = ChangeDisplaySettingsExW(device, ref dm, 0, CDS_UPDATEREGISTRY | CDS_NORESET, 0);
        return r1 != 0 ? r1 : ChangeDisplaySettingsExW(null, 0, 0, 0, 0);
    }
}
