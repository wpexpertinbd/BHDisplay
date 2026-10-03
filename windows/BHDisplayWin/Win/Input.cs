// Capturing local keyboard/mouse (low-level hooks) and replaying the peer's input (SendInput).
using System.Runtime.InteropServices;
using BHDisplay.Core;
using static BHDisplay.Win.Native;

namespace BHDisplay.Win;

internal static class Tag { public const nuint Injected = 0x42484453; }   // "BHDS" in dwExtraInfo: our own injections

/// Low-level hooks on the UI thread. Idle: watch for the pointer reaching the sharing edge.
/// Capturing: swallow all keyboard/mouse input and hand it to the controller.
internal sealed class Capture : IDisposable
{
    public int Edge = 0;                                   // 0 = Mac is on the left, 1 = right
    public bool WatchEdge;                                 // only while the shared monitor shows this PC
    public bool Capturing { get; private set; }
    public Func<float, bool>? EdgeHit;                     // return true to start capturing
    public Action<ShareMsg>? Captured;
    public Action<ushort>? LocalHotkey;                    // Ctrl+Alt+Win + key while capturing (HID usage)

    private nint _kb, _ms;
    private readonly HookProc _kbProc, _msProc;            // keep delegates alive
    private POINT _park, _last;
    private bool _ctrl, _alt, _win;
    // Keys physically held on this PC when capturing began (e.g. Ctrl+Alt+Win of the switch shortcut):
    // their releases must reach Windows, or Windows keeps them "pressed" for good.
    private readonly HashSet<uint> _heldHere = [];

    public Capture() { _kbProc = KeyboardProc; _msProc = MouseProc; }

    /// Windows silently removes a low-level hook that once took too long: re-install on demand.
    public bool Restart()
    {
        if (Capturing) return true;
        if (_kb != 0) UnhookWindowsHookEx(_kb);
        if (_ms != 0) UnhookWindowsHookEx(_ms);
        _kb = _ms = 0;
        return Start();
    }

    public bool Start()
    {
        if (_kb != 0) return true;
        var mod = GetModuleHandleW(null);
        _ms = SetWindowsHookExW(WH_MOUSE_LL, _msProc, mod, 0);
        _kb = SetWindowsHookExW(WH_KEYBOARD_LL, _kbProc, mod, 0);
        return _kb != 0 && _ms != 0;
    }

    public void Begin(int startY)
    {
        if (Capturing) return;
        Capturing = true;
        _park = Screens.Center(Edge);                      // away from edges so deltas never clamp
        SetCursorPos(_park.X, _park.Y);
        _ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;    // VK_CONTROL / VK_MENU / VK_LWIN, VK_RWIN
        _alt = (GetAsyncKeyState(0x12) & 0x8000) != 0;
        _win = (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;
        _heldHere.RemoveWhere(vk => (GetAsyncKeyState((int)vk) & 0x8000) == 0);   // drop any missed release
    }

    public void End(float? position)
    {
        if (!Capturing) return;
        Capturing = false;
        if (position is { } p)
        {
            var e = Screens.EntryPoint(Edge, p);
            SetCursorPos(e.X, e.Y);
        }
    }

    // Dragging across would leave the button "down" here (its release would go to the Mac).
    private static bool AnyButtonDown() =>
        new[] { 0x01, 0x02, 0x04, 0x05, 0x06 }.Any(vk => (GetAsyncKeyState(vk) & 0x8000) != 0);

    private nint MouseProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode < 0) return CallNextHookEx(_ms, nCode, wParam, lParam);
        var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        if (m.dwExtraInfo == Tag.Injected) return CallNextHookEx(_ms, nCode, wParam, lParam);
        int msg = (int)wParam;

        if (!Capturing)
        {
            if (msg == WM_MOUSEMOVE)
            {
                var towards = Edge == 0 ? m.pt.X <= _last.X : m.pt.X >= _last.X;
                _last = m.pt;
                if (WatchEdge && towards && !AnyButtonDown() && Screens.Hit(m.pt, Edge) is { } pos && EdgeHit?.Invoke(pos) == true)
                {
                    Begin(m.pt.Y);
                    return 1;
                }
            }
            return CallNextHookEx(_ms, nCode, wParam, lParam);
        }

        switch (msg)
        {
            case WM_MOUSEMOVE:
                int dx = m.pt.X - _park.X, dy = m.pt.Y - _park.Y;
                if (dx != 0 || dy != 0) Captured?.Invoke(new ShareMsg.Move((short)Math.Clamp(dx, short.MinValue, short.MaxValue), (short)Math.Clamp(dy, short.MinValue, short.MaxValue)));
                break;
            case WM_LBUTTONDOWN or WM_LBUTTONUP: Captured?.Invoke(new ShareMsg.Button(1, msg == WM_LBUTTONDOWN)); break;
            case WM_RBUTTONDOWN or WM_RBUTTONUP: Captured?.Invoke(new ShareMsg.Button(2, msg == WM_RBUTTONDOWN)); break;
            case WM_MBUTTONDOWN or WM_MBUTTONUP: Captured?.Invoke(new ShareMsg.Button(3, msg == WM_MBUTTONDOWN)); break;
            case WM_XBUTTONDOWN or WM_XBUTTONUP:
                var x = (m.mouseData >> 16) & 0xFFFF;
                Captured?.Invoke(new ShareMsg.Button(x == XBUTTON1 ? (byte)4 : (byte)5, msg == WM_XBUTTONDOWN));
                break;
            case WM_MOUSEWHEEL: Captured?.Invoke(new ShareMsg.Scroll(0, (short)(m.mouseData >> 16))); break;
            case WM_MOUSEHWHEEL: Captured?.Invoke(new ShareMsg.Scroll((short)(m.mouseData >> 16), 0)); break;
        }
        return 1;                                          // capturing: nothing reaches this PC
    }

    private nint KeyboardProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode < 0) return CallNextHookEx(_kb, nCode, wParam, lParam);
        var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        if (k.dwExtraInfo == Tag.Injected) return CallNextHookEx(_kb, nCode, wParam, lParam);
        bool down = (int)wParam is WM_KEYDOWN or WM_SYSKEYDOWN;
        if (!Capturing)
        {
            if (down) _heldHere.Add(k.vkCode); else _heldHere.Remove(k.vkCode);
            return CallNextHookEx(_kb, nCode, wParam, lParam);
        }
        if (!down && _heldHere.Remove(k.vkCode)) return CallNextHookEx(_kb, nCode, wParam, lParam);   // pressed here: release here
        if (down && _heldHere.Contains(k.vkCode)) return 1;                                         // its auto-repeat: drop
        var hid = KeyMap.ToHid(k.vkCode, k.scanCode, (k.flags & LLKHF_EXTENDED) != 0);
        switch (hid)
        {
            case 0xE0 or 0xE4: _ctrl = down; break;
            case 0xE2 or 0xE6: _alt = down; break;
            case 0xE3 or 0xE7: _win = down; break;
        }
        if (down && _ctrl && _alt && _win && hid is { } h && h is 0x16 or 0x1E or 0x1F or 0x20 or 0x29)
        {
            LocalHotkey?.Invoke(h);                        // S / 1 / 2 / 3 / Esc stay on this PC
            return 1;
        }
        if (hid is { } u) Captured?.Invoke(new ShareMsg.Key(u, down));
        return 1;
    }

    public void Dispose()
    {
        if (_kb != 0) UnhookWindowsHookEx(_kb);
        if (_ms != 0) UnhookWindowsHookEx(_ms);
        _kb = _ms = 0;
    }
}

/// Replays the peer's input. Every injected event carries Tag.Injected so our hooks ignore it.
internal sealed class Emulator
{
    public int Edge = 0;                                   // edge facing the Mac
    public Action<float>? Left;                            // controlled pointer pushed back out through Edge
    public bool Active { get; private set; }

    private readonly HashSet<ushort> _keys = [];
    private readonly HashSet<byte> _buttons = [];

    private static void Send(params INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    private static INPUT Mouse(uint flags, int dx = 0, int dy = 0, uint data = 0) => new()
    {
        type = INPUT_MOUSE,
        u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags, dwExtraInfo = Tag.Injected } },
    };

    private bool _handBack;                                // false = take-over: no hand-back at an edge

    /// takeover: the pointer is not moved and never handed back by position (the Mac's screen is hidden).
    public void Enter(float position, bool takeover = false)
    {
        Active = true;
        _handBack = !takeover;
        if (!takeover) MoveTo(Screens.EntryPoint(Edge, position));
    }

    public void Leave() { ReleaseAll(); Active = false; }

    private static void MoveTo(POINT p)
    {
        var (l, t, w, h) = Screens.Virtual();
        int ax = (int)((p.X - l) * 65535L / Math.Max(w - 1, 1)), ay = (int)((p.Y - t) * 65535L / Math.Max(h - 1, 1));
        Send(Mouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, ax, ay));
    }

    public void Move(short dx, short dy)
    {
        if (!Active) return;
        GetCursorPos(out var cur);
        var target = new POINT { X = cur.X + dx, Y = cur.Y + dy };
        var p = Screens.Clamp(target);
        MoveTo(p);
        bool pushingOut = _handBack && (Edge == 0 ? dx < 0 : dx > 0);
        // Decide from where the Mac's mouse is sending the pointer, so hand-back works even when the real cursor
        // can't move (lock screen, an app confining it).
        if (pushingOut && (Screens.Hit(target, Edge) ?? Screens.Hit(p, Edge)) is { } pos) { Leave(); Left?.Invoke(pos); }
    }

    public void Button(byte b, bool down)
    {
        if (!Active) return;
        (uint flags, uint data) = b switch
        {
            1 => (down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP, 0u),
            2 => (down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP, 0u),
            3 => (down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP, 0u),
            4 => (down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, XBUTTON1),
            _ => (down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, XBUTTON2),
        };
        if (down) _buttons.Add(b); else _buttons.Remove(b);
        Send(Mouse(flags, data: data));
    }

    public void Scroll(short dx, short dy)
    {
        if (!Active) return;
        if (dy != 0) Send(Mouse(MOUSEEVENTF_WHEEL, data: unchecked((uint)dy)));
        if (dx != 0) Send(Mouse(MOUSEEVENTF_HWHEEL, data: unchecked((uint)dx)));
    }

    public void Key(ushort usage, bool down)
    {
        if (!Active) return;
        var input = new INPUT { type = INPUT_KEYBOARD };
        if (usage == KeyMap.HidNumLock || usage == KeyMap.HidPause)
        {
            input.u.ki = new KEYBDINPUT { wVk = (ushort)(usage == KeyMap.HidNumLock ? KeyMap.VkNumLock : KeyMap.VkPause),
                dwFlags = down ? 0 : KEYEVENTF_KEYUP, dwExtraInfo = Tag.Injected };
        }
        else if (KeyMap.ToScan(usage) is { } scan)
        {
            uint f = KEYEVENTF_SCANCODE | (down ? 0 : KEYEVENTF_KEYUP) | ((scan & 0xE000) != 0 ? KEYEVENTF_EXTENDEDKEY : 0);
            input.u.ki = new KEYBDINPUT { wScan = (ushort)(scan & 0xFF), dwFlags = f, dwExtraInfo = Tag.Injected };
        }
        else return;
        if (down) _keys.Add(usage); else _keys.Remove(usage);
        Send(input);
    }

    public void ReleaseAll()
    {
        foreach (var b in _buttons.ToArray()) Button(b, false);
        foreach (var k in _keys.ToArray()) Key(k, false);
        _buttons.Clear(); _keys.Clear();
    }
}
