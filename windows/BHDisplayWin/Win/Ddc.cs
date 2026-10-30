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

    /// The monitors the user marked as also connected to the Mac, by MonitorEntry.Key ("sn:<EDID serial>"). It is a
    /// LIST so a second monitor of the same model (one at the office, one at home) is marked once and then recognised
    /// in both places — a replacement unit has a different serial. Empty = none marked.
    public static volatile string[] PreferredKeys = new string[0];
    /// True after the last lookup found several monitors and none is marked (the user must choose).
    public static volatile bool NeedsChoice;
    /// True after the last lookup found no marked monitor among the ones that answered — a marked monitor is off,
    /// unplugged, or was swapped for another unit. Nothing is acted on; the user is told to mark this one too.
    public static volatile bool MarkedMissing;

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
    /// How many monitors on Windows' desktop RIGHT NOW are marked as connected to the Mac. user32 + registry only, no
    /// DDC, so it takes milliseconds: a switch request from the Mac is answered from a fresh count instead of whatever
    /// the last DDC lookup happened to leave behind (a mark just ticked, or a monitor just plugged back in).
    /// A monitor whose output we turned off is not on the desktop — callers only ask while nothing is turned off.
    public static int MarkedOnDesktop(string[] marked)
    {
        if (marked.Length == 0) return 0;
        // Counted exactly the way OpenAll() builds its list — one entry per PHYSICAL monitor behind each HMONITOR, all
        // carrying that display's EDID serial — so this count and Choose() can never disagree (e.g. cloned displays:
        // two physical monitors behind one HMONITOR count as two, which Choose() also sees as ambiguous).
        var found = new List<(string Device, uint Physical)>();
        EnumDisplayMonitors(0, 0, (nint m, nint _, ref RECT _, nint _) =>
        {
            var mi = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            if (GetNumberOfPhysicalMonitorsFromHMONITOR(m, out var count) && count > 0
                && GetMonitorInfoEx(m, ref mi) && !string.IsNullOrEmpty(mi.szDevice))
                found.Add((mi.szDevice, count));
            return true;
        }, 0);
        int n = 0;
        foreach (var (device, physical) in found)
        {
            var sn = EdidSerial(device);
            if (sn.Length > 0 && marked.Contains("sn:" + sn, StringComparer.Ordinal)) n += (int)physical;
        }
        return n;
    }

    /// Reads from the registry only — no DDC traffic — so it is safe to call on the UI thread.
    public static string EdidSerial(string device)
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
            if (answering.Count == 0)
            {   // Nothing answered DDC at all — the monitor is silent (often for seconds after a switch), which is not
                // the same as "the marked monitor isn't here" (2026-10-30 20:54:22 said so at start-up, wrongly).
                // Switch requests are decided from a fresh desktop count, not from this flag.
                if (key is null) MarkedMissing = false;
                return default;
            }
            // Exactly one match or nothing: never act on a monitor we can't tell apart from another one.
            MonitorEntry? Only(string k) { var ms = answering.Where(x => x.Key == k).ToList(); return ms.Count == 1 ? ms[0] : null; }
            if (key is not null)
            {
                var m = Only(key);
                return m is null ? default : action(m.Handle, m.Desc);
            }
            // Which monitor may be acted on (rules + tests in Core/MonitorMarks.cs).
            switch (BHDisplay.Core.MonitorMarks.Choose(answering.Select(x => x.Key).ToList(), PreferredKeys, out var pick))
            {
                case BHDisplay.Core.MarkChoice.Chosen:
                    NeedsChoice = false; MarkedMissing = false;
                    var m2 = answering.First(x => x.Key == pick);
                    return action(m2.Handle, m2.Desc);
                case BHDisplay.Core.MarkChoice.Missing:
                    NeedsChoice = false; MarkedMissing = true;      // swapped, off, or at the other desk
                    return default;
                case BHDisplay.Core.MarkChoice.Ambiguous:
                    // Both marked monitors are here (both desks' monitors on one PC): never pick the wrong one — but
                    // this is still "we cannot act", so a switch request must be declined, not accepted and dropped.
                    NeedsChoice = false; MarkedMissing = false;
                    return default;
            }
            MarkedMissing = false;
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
    public int MacEdge { get; set; } = 0;                  // side of the other computer: 0 = left of this PC, 1 = right
    public bool PeerSideChosen { get; set; }               // the user picked the side (else learned from the other computer)
    public bool SharesMonitorWithMac { get; set; }         // a Mac has told us it shares a monitor with this PC
    public Dictionary<string, string> Paired { get; set; } = new();
    public Dictionary<string, string> PeerHosts { get; set; } = new();   // fingerprint → last IPv4 the peer was reached at
    public byte MacPort { get; set; } = 0x12;              // updated from the Mac (MONITOR_PORTS)
    public byte PcPort { get; set; } = 0x0F;
    public string SharedMonitorSerial { get; set; } = "";   // legacy single choice; migrated into SharedMonitorKeys
    public List<string> SharedMonitorKeys { get; set; } = new();   // every monitor marked as also connected to the Mac
    public bool Logging { get; set; } = true;               // "Keep a Log"
    public int LogKeepDays { get; set; } = 7;               // delete log entries older than 3 or 7 days
    public bool TurnOffWhenMac { get; set; } = true;      // turn Windows' output to the shared monitor off while it shows the Mac
    public string DetachedDevice { get; set; } = "";
    /// WHICH monitor was turned off, not just which display slot (\\.\DISPLAYn). A slot is reused by whatever
    /// monitor is plugged into that port, so without this the saved resolution/refresh of the monitor we turned
    /// off could be forced onto a different monitor later — and written into the registry.
    public string DetachedKey { get; set; } = "";
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
            // Carries a single mark from an older build into the list, drops anything malformed, caps the count.
            s.SharedMonitorKeys = BHDisplay.Core.MonitorMarks.Normalise(s.SharedMonitorKeys, s.SharedMonitorSerial);
            s.SharedMonitorSerial = s.SharedMonitorKeys.FirstOrDefault() ?? "";
            if (!Ddc.IsInput(s.MacPort)) s.MacPort = 0x12;    // never trust a hand-edited value
            if (!Ddc.IsInput(s.PcPort)) s.PcPort = 0x0F;
            if (s.MacEdge is not (0 or 1)) s.MacEdge = 0;
            s.DetachedDevice ??= ""; s.RestorePrimary ??= "";
            if (s.RestorePrimary.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(s.RestorePrimary, @"^\\\\\.\\DISPLAY\d{1,3}$")) s.RestorePrimary = "";
            if (s.DetachedDevice.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(s.DetachedDevice, @"^\\\\\.\\DISPLAY\d{1,3}$")) s.DetachedDevice = "";
            s.DetachedKey ??= "";
            if (s.DetachedKey.Length > 0 && !BHDisplay.Core.MonitorMarks.IsKey(s.DetachedKey)) s.DetachedKey = "";
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
