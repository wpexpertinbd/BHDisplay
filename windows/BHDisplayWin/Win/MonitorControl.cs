// Everything the dashboard reads and writes on the monitor (DDC/CI via dxva2), plus what Windows knows about it.
// Codes and names match the Mac app (Sources/Model.swift) — verified on the XG2409A's on-screen menu.
using System.Management;
using static BHDisplay.Win.Native;

namespace BHDisplay.Win;

internal static class Vcp
{
    public const byte Brightness = 0x10, Contrast = 0x12, Sharpness = 0x87, Volume = 0x62, Mute = 0x8D,
        ColorPreset = 0x14, Red = 0x16, Green = 0x18, Blue = 0x1A, ViewMode = 0xDC, AutoDetect = 0x33,
        BlueLight = 0xE2, Firmware = 0xC9, Input = 0x60, FactoryReset = 0x04;

    public static readonly byte[] Panel = { Input, Brightness, Contrast, Sharpness, Volume, Mute, ColorPreset, Red, Green, Blue,
                                            ViewMode, AutoDetect, BlueLight, Firmware };
    /// The only codes the dashboard may write (reset has its own confirmed path).
    public static readonly HashSet<byte> Writable = new() { Brightness, Contrast, Sharpness, Volume, Mute, ColorPreset, Red, Green, Blue,
                                                           ViewMode, AutoDetect, BlueLight };
}

internal static class Choices
{
    public static readonly (uint Code, string Name)[] ColorTemp =
        { (0x01, "sRGB"), (0x08, "Bluish"), (0x06, "Cool"), (0x05, "Native"), (0x04, "Warm"), (0x0B, "User Color") };
    public static readonly (uint Code, string Name)[] ViewMode =
        { (0x00, "Standard"), (0x30, "FPS Game"), (0x31, "RTS Game"), (0x32, "MOBA Game"), (0x03, "Movie"), (0x33, "Web"),
          (0x34, "Text"), (0x35, "MAC"), (0x36, "Mono"), (0x3E, "Custom") };
    public const uint UserColor = 0x0B;
}

internal static partial class Ddc
{
    /// Reads several codes with the monitor opened once. Missing keys = the monitor didn't answer that code.
    public static Dictionary<byte, (uint Cur, uint Max)> ReadMany(IEnumerable<byte> codes, string? key = null) =>
        WithMonitor((h, _desc) =>
        {
            var d = new Dictionary<byte, (uint, uint)>();
            foreach (var c in codes)
                if (GetVCPFeatureAndVCPFeatureReply(h, c, out _, out var cur, out var max)) d[c] = (cur, max);
            return d;
        }, key) ?? new Dictionary<byte, (uint, uint)>();

    public static bool Write(byte code, uint value, string? key = null)
    {
        if (!Vcp.Writable.Contains(code)) return false;
        return WithMonitor<bool?>((h, _desc) => SetVCPFeature(h, code, value), key) ?? false;
    }

    /// Restore factory settings — only ever called after the user confirmed a dialog naming the monitor.
    public static bool FactoryReset(string? key = null) => WithMonitor<bool?>((h, _desc) => SetVCPFeature(h, Vcp.FactoryReset, 1), key) ?? false;
}

/// What Windows knows about the ViewSonic (EDID via WMI) and the current display mode.
internal sealed class MonitorDetails
{
    public string Name = "Monitor", Serial = "—", Manufactured = "—", Resolution = "—";

    /// `serial`/`device`: the monitor to describe (the ViewSonic if unknown).
    public static MonitorDetails Read(string serial = "", string? device = null)
    {
        var m = new MonitorDetails();
        try
        {
            using var q = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorID");
            foreach (ManagementObject o in q.Get())
            {
                string Text(string p) => o[p] is ushort[] a ? new string(a.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim() : "";
                var maker = Text("ManufacturerName");
                if (serial.Length > 0 && Text("SerialNumberID") != serial) continue;          // the one asked for
                if (serial.Length == 0 && maker.Length > 0 && maker != "VSC" && m.Name != "Monitor") continue;   // prefer the ViewSonic
                m.Name = Text("UserFriendlyName") is { Length: > 0 } n ? n : m.Name;
                m.Serial = Text("SerialNumberID") is { Length: > 0 } s ? s : m.Serial;
                if (o["WeekOfManufacture"] is byte w && o["YearOfManufacture"] is ushort y && y > 1990) m.Manufactured = $"Week {w}, {y}";
                if (maker == "VSC") break;
            }
        }
        catch (Exception) { /* WMI unavailable: leave the defaults */ }
        try
        {
            var dm = new DEVMODE { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettingsW(device, -1, ref dm))
                m.Resolution = $"{dm.dmPelsWidth} × {dm.dmPelsHeight} at {dm.dmDisplayFrequency}Hz";
        }
        catch (Exception) { }
        return m;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct DEVMODE
    {
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? device, int mode, ref DEVMODE dm);
}

/// One background thread for all DDC traffic (the monitor's bus is slow and calls block). Slider writes are
/// coalesced: dragging sends only the latest value per code. Results come back on the UI thread.
internal sealed class DdcWorker : IDisposable
{
    private readonly SynchronizationContext _ui;
    private readonly object _gate = new();
    private readonly Dictionary<(string? Key, byte Code), uint> _writes = new();
    private readonly Queue<Action> _jobs = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _stop;

    public DdcWorker(SynchronizationContext ui)
    {
        _ui = ui;
        _thread = new Thread(Run) { IsBackground = true, Name = "BHDisplay DDC" };
        _thread.Start();
    }

    public void Write(byte code, uint value, string? key = null) { lock (_gate) _writes[(key, code)] = value; _wake.Set(); }

    /// Runs `work` on the DDC thread (after pending writes), then `done` on the UI thread.
    public void Do<T>(Func<T> work, Action<T> done)
    {
        lock (_gate) _jobs.Enqueue(() => { var r = work(); _ui.Post(_ => done(r), null); });
        _wake.Set();
    }

    private void Run()
    {
        while (!_stop)
        {
            _wake.WaitOne();
            while (!_stop)
            {
                KeyValuePair<(string? Key, byte Code), uint>[] writes; Action? job = null;
                lock (_gate)
                {
                    writes = _writes.ToArray(); _writes.Clear();
                    if (writes.Length == 0 && _jobs.Count > 0) job = _jobs.Dequeue();
                }
                if (writes.Length == 0 && job is null) break;
                foreach (var w in writes) { try { Ddc.Write(w.Key.Code, w.Value, w.Key.Key); } catch (Exception e) { Log.Write("DDC write failed: " + e.Message); } }
                try { job?.Invoke(); } catch (Exception e) { Log.Write("DDC job failed: " + e.Message); }
            }
        }
    }

    public void Dispose() { _stop = true; _wake.Set(); }
}
