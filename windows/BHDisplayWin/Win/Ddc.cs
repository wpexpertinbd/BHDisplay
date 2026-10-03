// Monitor input switching over DDC/CI with Windows' Monitor Configuration API (dxva2), and app settings.
using System.Runtime.InteropServices;
using System.Text.Json;
using static BHDisplay.Win.Native;

namespace BHDisplay.Win;

internal static class Ddc
{
    public const byte VcpInput = 0x60;
    public static readonly (byte Code, string Name)[] Inputs = [(0x0F, "DisplayPort"), (0x12, "HDMI 1"), (0x11, "HDMI 2")];
    public static string NameOf(uint code) => Inputs.FirstOrDefault(i => i.Code == (code & 0xFF)).Name ?? $"Input 0x{code & 0xFF:X2}";
    public static bool IsInput(uint code) => Inputs.Any(i => i.Code == code);

    /// Runs `action` on the first physical monitor that answers VCP 0x60, preferring a ViewSonic.
    private static T? WithMonitor<T>(Func<nint, string, T?> action)
    {
        var handles = new List<nint>();
        EnumDisplayMonitors(0, 0, (nint m, nint _, ref RECT _, nint _) => { handles.Add(m); return true; }, 0);
        var found = new List<(nint H, string Desc, PHYSICAL_MONITOR[] Arr)>();
        try
        {
            foreach (var hm in handles)
            {
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hm, out var n) || n == 0) continue;
                var arr = new PHYSICAL_MONITOR[n];
                if (!GetPhysicalMonitorsFromHMONITOR(hm, n, arr)) continue;
                foreach (var pm in arr) found.Add((pm.hPhysicalMonitor, pm.szPhysicalMonitorDescription ?? "", arr));
            }
            var ordered = found.OrderByDescending(f => f.Desc.Contains("ViewSonic", StringComparison.OrdinalIgnoreCase)
                                                    || f.Desc.Contains("XG2409", StringComparison.OrdinalIgnoreCase));
            foreach (var f in ordered)
            {
                if (GetVCPFeatureAndVCPFeatureReply(f.H, VcpInput, out _, out _, out _)) return action(f.H, f.Desc);
            }
            return default;
        }
        finally
        {
            foreach (var arr in found.Select(f => f.Arr).Distinct()) DestroyPhysicalMonitors((uint)arr.Length, arr);
        }
    }

    public static uint? CurrentInput() =>
        WithMonitor<uint?>((h, _) => GetVCPFeatureAndVCPFeatureReply(h, VcpInput, out _, out var cur, out _) ? cur & 0xFF : null);

    public static string? MonitorName() => WithMonitor<string>((_, d) => d);

    /// Switch the monitor input. Refuses anything that is not one of the monitor's real inputs.
    public static bool Switch(byte code)
    {
        if (!IsInput(code)) return false;
        return WithMonitor<bool?>((h, _) =>
        {
            if (GetVCPFeatureAndVCPFeatureReply(h, VcpInput, out _, out var cur, out _) && (cur & 0xFF) == code) return true;
            return SetVCPFeature(h, VcpInput, code);
        }) ?? false;
    }
}

/// Settings + identity under %LOCALAPPDATA%\BHDisplay. The identity key is DPAPI-protected (current user).
internal sealed class Settings
{
    public bool Sharing { get; set; } = true;
    public int MacEdge { get; set; } = 0;                  // the Mac is on the left of this PC
    public Dictionary<string, string> Paired { get; set; } = [];
    public byte MacPort { get; set; } = 0x12;              // updated from the Mac (MONITOR_PORTS)
    public byte PcPort { get; set; } = 0x0F;

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BHDisplay");
    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
            if (!Ddc.IsInput(s.MacPort)) s.MacPort = 0x12;    // never trust a hand-edited value
            if (!Ddc.IsInput(s.PcPort)) s.PcPort = 0x0F;
            if (s.MacEdge is not (0 or 1)) s.MacEdge = 0;
            return s;
        }
        catch { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, FilePath, true);
    }

    public static byte[]? LoadIdentity()
    {
        try { return Dpapi.Unprotect(File.ReadAllBytes(Path.Combine(Dir, "identity"))); }
        catch { return null; }
    }

    public static void SaveIdentity(byte[] blob)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllBytes(Path.Combine(Dir, "identity"), Dpapi.Protect(blob));
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
