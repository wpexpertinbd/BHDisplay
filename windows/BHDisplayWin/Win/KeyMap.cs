// USB HID usage IDs (Keyboard/Keypad page 0x07) ↔ PC scan codes (set 1; 0xE0xx = extended).
// Source: USB HID Usage Tables and Microsoft's "Keyboard Scan Code Specification".
using BHDisplay.Core;

namespace BHDisplay.Win;

internal static class KeyMap
{
    private static readonly (ushort Hid, ushort Scan)[] Pairs =
    [
        (0x04, 0x1E), (0x05, 0x30), (0x06, 0x2E), (0x07, 0x20), (0x08, 0x12), (0x09, 0x21), (0x0A, 0x22), (0x0B, 0x23),
        (0x0C, 0x17), (0x0D, 0x24), (0x0E, 0x25), (0x0F, 0x26), (0x10, 0x32), (0x11, 0x31), (0x12, 0x18), (0x13, 0x19),
        (0x14, 0x10), (0x15, 0x13), (0x16, 0x1F), (0x17, 0x14), (0x18, 0x16), (0x19, 0x2F), (0x1A, 0x11), (0x1B, 0x2D),
        (0x1C, 0x15), (0x1D, 0x2C),
        (0x1E, 0x02), (0x1F, 0x03), (0x20, 0x04), (0x21, 0x05), (0x22, 0x06), (0x23, 0x07), (0x24, 0x08), (0x25, 0x09),
        (0x26, 0x0A), (0x27, 0x0B),
        (0x28, 0x1C), (0x29, 0x01), (0x2A, 0x0E), (0x2B, 0x0F), (0x2C, 0x39), (0x2D, 0x0C), (0x2E, 0x0D), (0x2F, 0x1A),
        (0x30, 0x1B), (0x31, 0x2B), (0x33, 0x27), (0x34, 0x28), (0x35, 0x29), (0x36, 0x33), (0x37, 0x34), (0x38, 0x35),
        (0x39, 0x3A),
        (0x3A, 0x3B), (0x3B, 0x3C), (0x3C, 0x3D), (0x3D, 0x3E), (0x3E, 0x3F), (0x3F, 0x40), (0x40, 0x41), (0x41, 0x42),
        (0x42, 0x43), (0x43, 0x44), (0x44, 0x57), (0x45, 0x58),
        (0x46, 0xE037), (0x47, 0x46),
        (0x49, 0xE052), (0x4A, 0xE047), (0x4B, 0xE049), (0x4C, 0xE053), (0x4D, 0xE04F), (0x4E, 0xE051),
        (0x4F, 0xE04D), (0x50, 0xE04B), (0x51, 0xE050), (0x52, 0xE048),
        (0x54, 0xE035), (0x55, 0x37), (0x56, 0x4A), (0x57, 0x4E), (0x58, 0xE01C),
        (0x59, 0x4F), (0x5A, 0x50), (0x5B, 0x51), (0x5C, 0x4B), (0x5D, 0x4C), (0x5E, 0x4D), (0x5F, 0x47), (0x60, 0x48),
        (0x61, 0x49), (0x62, 0x52), (0x63, 0x53), (0x64, 0x56), (0x65, 0xE05D), (0x67, 0x59),
        (0x68, 0x64), (0x69, 0x65), (0x6A, 0x66), (0x6B, 0x67), (0x6C, 0x68), (0x6D, 0x69), (0x6E, 0x6A), (0x6F, 0x6B),
        (0x7F, 0xE020), (0x80, 0xE030), (0x81, 0xE02E),
        (0x85, 0x7E), (0x87, 0x73), (0x89, 0x7D),
        (0xE0, 0x1D), (0xE1, 0x2A), (0xE2, 0x38), (0xE3, 0xE05B), (0xE4, 0xE01D), (0xE5, 0x36), (0xE6, 0xE038), (0xE7, 0xE05C),
    ];

    public const ushort HidNumLock = 0x53, HidPause = 0x48;
    public const uint VkNumLock = 0x90, VkPause = 0x13;

    private static readonly Dictionary<ushort, ushort> ToScanMap = Pairs.ToDictionary(p => p.Hid, p => p.Scan);
    private static readonly Dictionary<ushort, ushort> ToHidMap = Pairs.GroupBy(p => p.Scan).ToDictionary(g => g.Key, g => g.First().Hid);

    public static ushort? ToScan(ushort hid) => ToScanMap.TryGetValue(hid, out var s) ? s : null;

    /// From a low-level keyboard hook. NumLock and Pause are reported ambiguously by scan code, so use the VK.
    public static ushort? ToHid(uint vk, uint scan, bool extended)
    {
        if (vk == VkNumLock) return HidNumLock;
        if (vk == VkPause) return HidPause;
        var key = (ushort)((extended ? 0xE000u : 0u) | (scan & 0xFFu));
        return ToHidMap.TryGetValue(key, out var h) ? h : null;
    }
}

/// Virtual-desktop geometry (screen coordinates, may be negative on multi-monitor setups).
internal static class Screens
{
    public static List<Native.RECT> All()
    {
        var list = new List<Native.RECT>();
        Native.EnumDisplayMonitors(0, 0, (nint m, nint _, ref Native.RECT _, nint _) =>
        {
            var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
            if (Native.GetMonitorInfoW(m, ref mi)) list.Add(mi.rcMonitor);
            return true;
        }, 0);
        return list;
    }

    // No monitor at all (the only one switched to the Mac and dropped): use a nominal desktop, never crash.
    private static (int L, int T, int R, int B) Union(List<Native.RECT> ms) => ms.Count == 0 ? (0, 0, 1920, 1080) :
        (ms.Min(r => r.Left), ms.Min(r => r.Top), ms.Max(r => r.Right), ms.Max(r => r.Bottom));

    /// Monitors touching the outer `edge` (0 left, 1 right) of the desktop.
    public static List<Native.RECT> OnEdge(int edge)
    {
        var ms = All(); if (ms.Count == 0) return ms;
        var u = Union(ms);
        return ms.Where(r => edge == 0 ? r.Left <= u.L : r.Right >= u.R).ToList();
    }

    /// Position (0…1) along the edge if `p` is on the outer `edge`, else null.
    public static float? Hit(Native.POINT p, int edge)
    {
        foreach (var r in OnEdge(edge))
        {
            if (p.Y < r.Top || p.Y >= r.Bottom) continue;
            if (edge == 0 ? p.X <= r.Left : p.X >= r.Right - 1)
                return (float)(p.Y - r.Top) / Math.Max(r.Bottom - r.Top, 1);
        }
        return null;
    }

    public static Native.POINT EntryPoint(int edge, float pos)
    {
        var r = OnEdge(edge).OrderByDescending(m => (long)(m.Right - m.Left) * (m.Bottom - m.Top)).FirstOrDefault();
        var y = r.Top + (int)(Num.Clamp(pos, 0, 1) * (r.Bottom - r.Top - 1));
        // Land well inside, so a 1-pixel wobble can't send the pointer straight back (no flapping).
        return new Native.POINT { X = edge == 0 ? r.Left + 24 : r.Right - 25, Y = y };
    }

    public static Native.POINT Center(int edge)
    {
        var r = OnEdge(edge).FirstOrDefault();
        return new Native.POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 };
    }

    public static Native.POINT Clamp(Native.POINT p)
    {
        var ms = All();
        if (ms.Any(r => p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom)) return p;
        Native.POINT best = p; double bestD = double.MaxValue;
        foreach (var r in ms)
        {
            var q = new Native.POINT { X = Num.Clamp(p.X, r.Left, r.Right - 1), Y = Num.Clamp(p.Y, r.Top, r.Bottom - 1) };
            var d = Math.Pow(q.X - p.X, 2) + Math.Pow(q.Y - p.Y, 2);
            if (d < bestD) { best = q; bestD = d; }
        }
        return best;
    }

    public static (int L, int T, int W, int H) Virtual()
    {
        var u = Union(All());
        return (u.L, u.T, u.R - u.L, u.B - u.T);
    }
}
