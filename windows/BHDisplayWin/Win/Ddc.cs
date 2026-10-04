// Monitor input switching over DDC/CI with Windows' Monitor Configuration API (dxva2), and app settings.
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using static BHDisplay.Win.Native;

namespace BHDisplay.Win;

internal static partial class Ddc
{
    public const byte VcpInput = 0x60;
    public static readonly (byte Code, string Name)[] Inputs = [(0x0F, "DisplayPort"), (0x12, "HDMI 1"), (0x11, "HDMI 2")];
    public static string NameOf(uint code) => Inputs.FirstOrDefault(i => i.Code == (code & 0xFF)).Name ?? $"Input 0x{code & 0xFF:X2}";
    public static bool IsInput(uint code) => Inputs.Any(i => i.Code == code);

    /// A physical monitor that answers DDC (VCP 0x60), with what identifies it to the user.
    public sealed record MonitorEntry(nint Handle, string Desc, string Serial, string Device, RECT Bounds)
    {
        /// What the choice is remembered by: the EDID serial, or the Windows display slot if there is none.
        public string Key => Serial.Length > 0 ? "sn:" + Serial : "dev:" + Device;
    }

    /// Serial (EDID) of the monitor that is also connected to the Mac, chosen by the user when the PC has more than
    /// one monitor that answers DDC. Empty = not chosen.
    public static string PreferredSerial = "";
    /// True after the last lookup found several monitors and none matched PreferredSerial (the user must choose).
    public static volatile bool NeedsChoice;

    private static List<(MonitorEntry M, PHYSICAL_MONITOR[] Arr)> OpenAll()
    {
        var handles = new List<nint>();
        EnumDisplayMonitors(0, 0, (nint m, nint _, ref RECT _, nint _) => { handles.Add(m); return true; }, 0);
        var found = new List<(MonitorEntry, PHYSICAL_MONITOR[])>();
        foreach (var hm in handles)
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hm, out var n) || n == 0) continue;
            var arr = new PHYSICAL_MONITOR[n];
            if (!GetPhysicalMonitorsFromHMONITOR(hm, n, arr)) continue;
            var mi = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            GetMonitorInfoEx(hm, ref mi);
            var serial = EdidSerial(mi.szDevice ?? "");
            foreach (var pm in arr) found.Add((new MonitorEntry(pm.hPhysicalMonitor, pm.szPhysicalMonitorDescription ?? "", serial, mi.szDevice ?? "", mi.rcMonitor), arr));
        }
        return found;
    }

    /// EDID serial number of the monitor on a display (\\.\DISPLAYn), read from the registry copy of its EDID.
    private static string EdidSerial(string device)
    {
        try
        {
            var dd = new DISPLAY_DEVICE { cb = System.Runtime.InteropServices.Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevicesW(device, 0, ref dd, EDD_GET_DEVICE_INTERFACE_NAME)) return "";
            // \\?\DISPLAY#VSC1941#5&xxxx&0&UID4352#{guid} → SYSTEM\CurrentControlSet\Enum\DISPLAY\VSC1941\5&xxxx&0&UID4352
            var parts = (dd.DeviceID ?? "").Replace(@"\\?\", "").Split('#');
            if (parts.Length < 3) return "";
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{parts[0]}\{parts[1]}\{parts[2]}\Device Parameters");
            if (k?.GetValue("EDID") is not byte[] e || e.Length < 128) return "";
            for (int o = 54; o <= 108; o += 18)
                if (e[o] == 0 && e[o + 1] == 0 && e[o + 3] == 0xFF)
                    return System.Text.Encoding.ASCII.GetString(e, o + 5, 13).Split('\n')[0].Trim();
            uint num = (uint)(e[12] | e[13] << 8 | e[14] << 16 | e[15] << 24);
            return num == 0 ? "" : num.ToString();
        }
        catch (Exception) { return ""; }
    }

    /// The monitor shortcuts act on (the chosen shared one, or the only one) — with its Windows display name.
    public static MonitorEntry? FindShared() => WithMonitor((h, d) => OpenAllCached?.FirstOrDefault(m => m.Handle == h));
    [ThreadStatic] private static List<MonitorEntry>? OpenAllCached;

    /// Every monitor that answers DDC — for the "which one is connected to the Mac?" choice.
    /// One DDC conversation at a time across the whole app (tray, shortcuts, window): the bus is shared and slow.
    private static readonly object Bus = new();

    public static List<MonitorEntry> ListMonitors()
    {
        lock (Bus) return ListMonitorsLocked();
    }

    private static List<MonitorEntry> ListMonitorsLocked()
    {
        var all = OpenAll();
        // Numbered left to right (then top to bottom) as arranged in Windows' display settings — stable when cables move.
        try
        {
            return all.Where(f => GetVCPFeatureAndVCPFeatureReply(f.M.Handle, VcpInput, out _, out _, out _)).Select(f => f.M)
                      .OrderBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top).ToList();
        }
        finally { foreach (var arr in all.Select(f => f.Arr).Distinct()) DestroyPhysicalMonitors((uint)arr.Length, arr); }
    }

    /// Runs `action` on the shared monitor: the one the user chose (by EDID serial) when there are several; otherwise
    /// the only one that answers DDC, preferring a ViewSonic.
    /// With `key`, only that monitor (never another one if it is gone).
    private static T? WithMonitor<T>(Func<nint, string, T?> action, string? key = null)
    {
        lock (Bus) return WithMonitorLocked(action, key);
    }

    private static T? WithMonitorLocked<T>(Func<nint, string, T?> action, string? key)
    {
        var all = OpenAll();
        try
        {
            var answering = all.Where(f => GetVCPFeatureAndVCPFeatureReply(f.M.Handle, VcpInput, out _, out _, out _)).Select(f => f.M).ToList();
            OpenAllCached = answering;
            if (answering.Count == 0) return default;
            // Exactly one match or nothing: never act on a monitor we can't tell apart from another one.
            MonitorEntry? Only(string k) { var ms = answering.Where(x => x.Key == k).ToList(); return ms.Count == 1 ? ms[0] : null; }
            if (key is not null)
            {
                var m = Only(key);
                return m is null ? default : action(m.Handle, m.Desc);
            }
            if (PreferredSerial.Length > 0)
            {
                // A monitor was chosen: use it, or refuse (it may be off/unplugged) — never fall back to another one.
                NeedsChoice = false;
                var chosen = Only(PreferredSerial);
                return chosen is null ? default : action(chosen.Handle, chosen.Desc);
            }
            NeedsChoice = answering.Count > 1;
            if (answering.Count > 1) return default;          // several and none chosen: ask, don't guess
            return action(answering[0].Handle, answering[0].Desc);
        }
        finally
        {
            foreach (var arr in all.Select(f => f.Arr).Distinct()) DestroyPhysicalMonitors((uint)arr.Length, arr);
        }
    }

    public static uint? CurrentInput() =>
        WithMonitor<uint?>((h, _desc) => GetVCPFeatureAndVCPFeatureReply(h, VcpInput, out _, out var cur, out _) ? cur & 0xFF : null);

    public static string? MonitorName() => WithMonitor<string>((_, d) => d);

    /// Switch the monitor input. Refuses anything that is not one of the monitor's real inputs.
    public static bool Switch(byte code, string? key = null)
    {
        if (!IsInput(code)) return false;
        return WithMonitor<bool?>((h, _desc) =>
        {
            if (GetVCPFeatureAndVCPFeatureReply(h, VcpInput, out _, out var cur, out _) && (cur & 0xFF) == code) return true;
            return SetVCPFeature(h, VcpInput, code);
        }, key) ?? false;
    }
}

/// Settings + identity under %LOCALAPPDATA%\BHDisplay. The identity key is DPAPI-protected (current user).
internal sealed class Settings
{
    public bool Sharing { get; set; } = true;
    public int MacEdge { get; set; } = 0;                  // the Mac is on the left of this PC
    public Dictionary<string, string> Paired { get; set; } = new();
    public Dictionary<string, string> PeerHosts { get; set; } = new();   // fingerprint → last IPv4 the peer was reached at
    public byte MacPort { get; set; } = 0x12;              // updated from the Mac (MONITOR_PORTS)
    public byte PcPort { get; set; } = 0x0F;
    public string SharedMonitorSerial { get; set; } = "";   // which of several monitors is also on the Mac
    public bool Logging { get; set; } = true;               // "Keep a Log"
    public bool TurnOffWhenMac { get; set; } = true;      // turn Windows' output to the shared monitor off while it shows the Mac
    public string DetachedDevice { get; set; } = "";
    public string RestorePrimary { get; set; } = "";       // was the main display before we turned it off       // set while we have it off (restored at start if we crashed)
    public int DetachedX { get; set; } public int DetachedY { get; set; }
    public int DetachedW { get; set; } public int DetachedH { get; set; } public int DetachedHz { get; set; }
    public double MacMouseSpeed { get; set; } = 1;          // the Mac's mouse/trackpad on this PC (0.5…3)
    public double MacScrollSpeed { get; set; } = 1;         // (0.5…5)

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BHDisplay");
    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            var s = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
            s.Paired ??= new(); s.PeerHosts ??= new();
            if (!Ddc.IsInput(s.MacPort)) s.MacPort = 0x12;    // never trust a hand-edited value
            if (!Ddc.IsInput(s.PcPort)) s.PcPort = 0x0F;
            if (s.MacEdge is not (0 or 1)) s.MacEdge = 0;
            s.DetachedDevice ??= ""; s.RestorePrimary ??= "";
            if (s.RestorePrimary.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(s.RestorePrimary, @"^\\\\\.\\DISPLAY\d{1,3}$")) s.RestorePrimary = "";
            if (s.DetachedDevice.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(s.DetachedDevice, @"^\\\\\.\\DISPLAY\d{1,3}$")) s.DetachedDevice = "";
            s.MacMouseSpeed = double.IsNaN(s.MacMouseSpeed) ? 1 : Math.Max(0.5, Math.Min(3, s.MacMouseSpeed));
            s.MacScrollSpeed = double.IsNaN(s.MacScrollSpeed) ? 1 : Math.Max(0.5, Math.Min(5, s.MacScrollSpeed));
            s.Paired = s.Paired.Where(kv => kv.Key.Length == 64 && kv.Key.All(Uri.IsHexDigit)).ToDictionary(kv => kv.Key, kv => kv.Value);
            s.PeerHosts = s.PeerHosts.Where(kv => System.Net.IPAddress.TryParse(kv.Value, out var a)
                && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToDictionary(kv => kv.Key, kv => kv.Value);
            return s;
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        catch (Exception e)
        {   // Unreadable: keep the old file (it holds the pairing) instead of overwriting it with defaults.
            try { File.Copy(FilePath, FilePath + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true); } catch { }
            Log.Write("settings.json couldn't be read (" + e.Message + "); a copy was kept as settings.json.bad-*");
            return new();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(this));
        if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath);
    }

    public static byte[]? LoadIdentity()
    {
        try { return Dpapi.Unprotect(File.ReadAllBytes(Path.Combine(Dir, "identity"))); }
        catch { return null; }
    }

    public static void SaveIdentity(byte[] blob)
    {
        Directory.CreateDirectory(Dir);
        var path = Path.Combine(Dir, "identity");
        // Never destroy an existing identity (it is what the Mac knows this PC by): keep a copy first.
        if (File.Exists(path)) File.Copy(path, path + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
        File.WriteAllBytes(path, Dpapi.Protect(blob));
    }
}

/// Windows DPAPI (CryptProtectData), current-user scope: the identity key is only readable by this Windows user.
internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public nint Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string? desc, nint entropy, nint reserved, nint prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob input, nint desc, nint entropy, nint reserved, nint prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint p);
    private const int UiForbidden = 0x1;

    private static byte[] Run(byte[] data, bool protect)
    {
        var h = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new Blob { Size = data.Length, Data = h.AddrOfPinnedObject() };
            bool ok = protect ? CryptProtectData(ref input, "BHDisplay", 0, 0, 0, UiForbidden, out var o)
                              : CryptUnprotectData(ref input, 0, 0, 0, 0, UiForbidden, out o);
            if (!ok) throw new System.ComponentModel.Win32Exception();
            try { var r = new byte[o.Size]; Marshal.Copy(o.Data, r, 0, o.Size); return r; }
            finally { LocalFree(o.Data); }
        }
        finally { h.Free(); }
    }
    public static byte[] Protect(byte[] d) => Run(d, true);
    public static byte[] Unprotect(byte[] d) => Run(d, false);
}
