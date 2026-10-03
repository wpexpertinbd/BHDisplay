// BHDisplay for Windows — tray icon, monitor switching, and keyboard & mouse sharing with BHDisplay on the Mac.
// Moving the mouse never changes the monitor input; only the menu and Ctrl+Alt+Win+S / 1 / 2 / 3 do.
using System.Runtime.InteropServices;
using BHDisplay.Core;
using Microsoft.Win32;

namespace BHDisplay.Win;

internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly SynchronizationContext _ui;
    private readonly Settings _settings = Settings.Load();
    private readonly HotkeyWindow _hotkeys;
    private ShareIdentity? _id;
    private readonly ShareListener _listener = new();
    private readonly ShareDiscovery _discovery = new();
    private readonly Capture _capture = new();
    private readonly Emulator _emu = new();
    private ShareSession? _session;
    private (ShareSession S, bool Local, bool Remote)? _pairing;
    private readonly Dictionary<string, (Beacon B, DateTime Seen)> _unpaired = [];
    private readonly HashSet<string> _dialing = [];
    private DateTime _lastPrompt = DateTime.MinValue;
    private bool _running, _controlling, _controlled;
    private uint _clipSeq;
    private bool _showsPc;                                 // the shared monitor currently shows this PC
    private bool _paused;                                  // Ctrl+Alt+Win+Esc: keep input here until the monitor changes
    private string _status = "Off";
    private readonly System.Windows.Forms.Timer _reconnect = new() { Interval = 5000 };

    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();

    public TrayApp()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _icon = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
            Text = "BHDisplay", Visible = true, ContextMenuStrip = _menu,
        };
        _menu.Opening += (_, _) => BuildMenu();
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleMonitor(); };
        _hotkeys = new HotkeyWindow(OnHotkey);
        _clipSeq = GetClipboardSequenceNumber();

        _capture.Edge = _emu.Edge = _settings.MacEdge;
        _capture.EdgeHit = pos => EdgeHit(pos);
        _capture.Captured = m => { if (_controlling) _session?.Send(m); };
        _capture.LocalHotkey = h => OnLocalHotkey(h);
        _emu.Left = pos => PeerPointerLeft(pos);
        _reconnect.Tick += (_, _) => ReconnectKnownPeers();

        Log.Write("tray icon created");
        // Everything else runs once the message loop is pumping: low-level hooks are serviced by that loop,
        // and nothing here may delay the tray icon from responding.
        _ui.Post(_ => Startup(), null);
    }

    private void Startup()
    {
        Log.Write("startup: message loop running");
        if (FirstRun()) { SetStartWithWindows(true); _settings.Save(); Log.Write("first run: start with Windows on"); }
        if (_settings.Sharing) StartSharing();
        UpdateStatus();
        _icon.ShowBalloonTip(5000, "BHDisplay is running",
            "It lives in the system tray (click ^ next to the clock if you don't see it). Right-click the icon for options.", ToolTipIcon.Info);
        Log.Write("tray ready: " + _status);
    }

    private void Post(Action a) => _ui.Post(_ => a(), null);

    private static bool FirstRun() => !File.Exists(Path.Combine(Settings.Dir, "settings.json"));

    // ---------------- monitor ----------------

    private void ToggleMonitor()
    {
        var cur = Ddc.CurrentInput();
        Switch(cur == _settings.MacPort ? _settings.PcPort : _settings.MacPort);
    }

    private void Switch(byte code)
    {
        // Going back to the Mac: let the Mac do it — it may have turned its output off while the monitor
        // showed this PC, and must turn it on before the monitor switches (or the monitor sees no signal).
        if (code == _settings.MacPort && _session is not null)
        {
            _session.Send(new ShareMsg.SwitchRequest(code));
            Log.Write($"asked the Mac to switch the monitor to {Ddc.NameOf(code)}");
            return;
        }
        if (!Ddc.Switch(code))
        {
            _icon.ShowBalloonTip(3000, "BHDisplay", "Couldn't switch the monitor. Turn on Setup Menu ▸ DDC/CI on the monitor.", ToolTipIcon.Warning);
            return;
        }
        _session?.Send(new ShareMsg.MonitorShows(code));   // the Mac follows what the monitor shows
        MonitorNowShows(code);
    }

    /// "Input follows the monitor": shows the Mac → this PC's input goes to the Mac (take-over);
    /// shows this PC → keep input here, cross to the Mac at the edge facing it.
    private void MonitorNowShows(byte code)
    {
        bool showsPc = code == _settings.PcPort;
        if (showsPc != _showsPc) { _showsPc = showsPc; _paused = false; Log.Write($"monitor shows {(showsPc ? "this PC" : "the Mac")} ({Ddc.NameOf(code)})"); }
        ApplyMode();
    }

    private void ApplyMode()
    {
        if (!_running) return;
        _capture.WatchEdge = _showsPc;
        if (_showsPc)
        {
            if (_controlling) StopControlling();            // visible again: Windows input belongs here
        }
        else
        {
            if (_controlled) { _emu.Leave(); _controlled = false; }
            if (!_controlling && !_paused && _session is not null) StartTakeover();
        }
        UpdateStatus();
    }

    private void StartTakeover()
    {
        SendClipboardIfChanged();
        _session!.Send(new ShareMsg.Enter(4, 0.5f));
        _capture.Begin(0);
        _controlling = true;
        Log.Write("→ controlling the Mac (take-over: the monitor shows the Mac)");
    }

    private void StopControlling()
    {
        _session?.Send(new ShareMsg.ReleaseAll());
        _session?.Send(new ShareMsg.Leave(4, 0.5f));
        _capture.End(null);
        _controlling = false;
        Log.Write("← stopped controlling the Mac");
    }

    private void OnHotkey(int id)
    {
        switch (id)
        {
            case 1: ToggleMonitor(); break;
            case 2: Switch(0x0F); break;
            case 3: Switch(0x12); break;
            case 4: Switch(0x11); break;
        }
    }

    /// Ctrl+Alt+Win shortcuts pressed while this PC's input is going to the Mac.
    private void OnLocalHotkey(ushort hid)
    {
        switch (hid)
        {
            case 0x29: _paused = true; StopControlling(); UpdateStatus(); break;   // Esc: take this PC's input back
            case 0x16: ToggleMonitor(); break;     // S
            case 0x1E: Switch(0x0F); break;        // 1
            case 0x1F: Switch(0x12); break;        // 2
            case 0x20: Switch(0x11); break;        // 3
        }
    }

    // ---------------- sharing lifecycle ----------------

    private void StartSharing()
    {
        if (_running) return;
        try
        {
            Log.Write("sharing: loading identity");
            _id = ShareIdentity.LoadOrCreate(Settings.LoadIdentity, Settings.SaveIdentity, Environment.MachineName);
            Log.Write($"sharing: identity ok ({_id.Name}); installing input hooks");
            if (!_capture.Start()) { _status = "Can't capture input (hooks failed)"; Log.Write(_status); return; }
            Log.Write("sharing: hooks ok; listening on TCP " + Bhds.TcpPort);
            _listener.Session += s => Post(() => Adopt(s));
            _listener.Error += e => Post(() => { _status = e; Log.Write(e); UpdateStatus(keepStatus: true); });
            _listener.Start(_id);
            Log.Write("sharing: listening; starting discovery on UDP " + Bhds.UdpPort);
            _discovery.Found += b => Post(() => OnBeacon(b));
            _discovery.Start(_id);
            Log.Write("sharing: discovery started");
        }
        catch (Exception e)
        {
            _status = "Sharing couldn't start: " + e.Message;
            Log.Write("StartSharing failed: " + e);
            return;
        }
        _running = true;
        _reconnect.Start();
        UpdateStatus();
    }

    /// Discovery can be blocked (firewalls, multi-adapter PCs); TCP usually is not. While disconnected,
    /// dial every paired peer at its last known address.
    private void ReconnectKnownPeers()
    {
        if (!_running || _session is not null || _pairing is not null) return;
        foreach (var (fp, host) in _settings.PeerHosts)
            if (_settings.Paired.ContainsKey(fp) && _dialing.Add(host)) _ = Dial(host, Bhds.TcpPort, () => _dialing.Remove(host));
    }

    private bool IsPreferred(ShareSession s)
    {
        if (_id is null || s.Peer is null) return true;
        bool meLower = _id.DeviceId.AsSpan().SequenceCompareTo(s.Peer.DeviceId) < 0;
        return (s.Role == Role.Dialer) == meLower;
    }

    private void AskForAddress()
    {
        using var f = new Form { Text = "BHDisplay — connect to the Mac", Width = 380, Height = 160, FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen, MaximizeBox = false, MinimizeBox = false, TopMost = true };
        var label = new Label { Text = "IP address of the Mac running BHDisplay:", Left = 12, Top = 14, Width = 340 };
        var box = new TextBox { Left = 12, Top = 38, Width = 340, PlaceholderText = "192.168.0.123" };
        var ok = new Button { Text = "Connect", Left = 196, Top = 72, Width = 75, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 277, Top = 72, Width = 75, DialogResult = DialogResult.Cancel };
        f.Controls.AddRange([label, box, ok, cancel]); f.AcceptButton = ok; f.CancelButton = cancel;
        if (f.ShowDialog() != DialogResult.OK) return;
        if (!System.Net.IPAddress.TryParse(box.Text.Trim(), out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        { MessageBox.Show("That isn't an IPv4 address.", "BHDisplay"); return; }
        _status = $"Connecting to {ip}…"; UpdateStatus(keepStatus: true);
        _ = Dial(ip.ToString(), Bhds.TcpPort, () => { });
    }

    private static string LocalAddresses() => string.Join(", ",
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                     && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Select(a => a.Address.ToString()));

    private void StopSharing()
    {
        if (!_running) return;
        _running = false;
        _reconnect.Stop();
        _capture.End(null); _capture.Dispose();
        _emu.Leave(); _controlling = _controlled = false;
        _discovery.Stop(); _listener.Stop();
        _session?.Close("sharing turned off"); _session = null;
        _pairing?.S.Close("sharing turned off"); _pairing = null;
        _unpaired.Clear();
        _status = "Off";
        UpdateStatus();
    }

    private bool IsPaired(byte[] prefix) =>
        _settings.Paired.Keys.Any(k => Convert.FromHexString(k).AsSpan(0, 8).SequenceEqual(prefix));

    private readonly HashSet<string> _loggedBeacons = [];
    private void OnBeacon(Beacon b)
    {
        if (!_running || _id is null) return;
        var key = P256.Hex(b.DeviceId);
        if (_loggedBeacons.Add(key)) Log.Write($"discovered {b.Name} at {b.Host}:{b.Port} (paired: {IsPaired(b.FingerprintPrefix)})");
        if (IsPaired(b.FingerprintPrefix))
        {
            _unpaired.Remove(key);
            // Lower device id dials; the other side waits for it.
            if (_session is null && _id.DeviceId.AsSpan().SequenceCompareTo(b.DeviceId) < 0 && _dialing.Add(key))
            {
                _ = Dial(b.Host, b.Port, () => _dialing.Remove(key));
            }
        }
        else _unpaired[key] = (b, DateTime.UtcNow);
        foreach (var k in _unpaired.Where(kv => DateTime.UtcNow - kv.Value.Seen > TimeSpan.FromSeconds(10)).Select(kv => kv.Key).ToList())
            _unpaired.Remove(k);
    }

    private async Task Dial(string host, int port, Action done)
    {
        try { var s = await ShareSession.DialAsync(host, port, _id!); Post(() => Adopt(s)); }
        catch { }
        finally { _ = Task.Delay(15000).ContinueWith(_ => Post(done)); }
    }

    private void Adopt(ShareSession s)
    {
        s.Ready += x => Post(() => OnReady(x));
        s.Message += (x, m) => Post(() => OnMessage(x, m));
        s.Closed += (x, why) => Post(() => OnClosed(x, why));
        s.Start();
    }

    private void OnReady(ShareSession s)
    {
        Log.Write($"session ready: {s.Peer?.Name} @ {s.RemoteHost}");
        if (!_running) { s.Close("not running"); return; }
        var fp = P256.Hex(s.PeerFingerprint);
        if (_settings.Paired.ContainsKey(fp))
        {
            if (_session is { } old && old != s)
            {
                // Both sides may dial at once: keep the connection dialed by the lower device id (both apply this rule).
                if (!IsPreferred(s)) { s.Close("duplicate connection"); return; }
                old.Close("duplicate connection");
            }
            _session = s;
            _settings.PeerHosts[fp] = s.RemoteHost; _settings.Save();
            ApplyMode();
            UpdateStatus();
            return;
        }
        // Unpaired: one pairing at a time, at most one prompt every 5 s.
        if (_pairing is not null || DateTime.UtcNow - _lastPrompt < TimeSpan.FromSeconds(5)) { s.Close("busy pairing"); return; }
        _lastPrompt = DateTime.UtcNow;
        _pairing = (s, false, false);
        _ = Task.Delay(60000).ContinueWith(_ => Post(() => { if (_pairing?.S == s) s.Close("pairing timed out"); }));
        var name = s.Peer?.Name ?? "another computer";
        var code = s.PairCode[..3] + " " + s.PairCode[3..];
        var answer = MessageBox.Show(
            $"{name} ({s.RemoteHost}) wants to share keyboard and mouse with this PC.\n\nPairing code:  {code}\n\n" +
            "Pair only if the other computer shows exactly the same code. Once paired, its keyboard and mouse can control this PC.",
            $"BHDisplay — pair with “{name}”?", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2, MessageBoxOptions.DefaultDesktopOnly);
        if (_pairing?.S != s) return;                      // timed out or closed while the dialog was open
        if (answer == DialogResult.Yes)
        {
            _pairing = (s, true, _pairing.Value.Remote);
            s.Send(new ShareMsg.PairConfirm());
            FinishPairing();
        }
        else { s.Send(new ShareMsg.PairReject()); s.Close("pairing declined"); }
    }

    private void FinishPairing()
    {
        if (_pairing is not { Local: true, Remote: true } p) return;
        _pairing = null;
        _settings.Paired[P256.Hex(p.S.PeerFingerprint)] = p.S.Peer?.Name ?? "Mac";
        _settings.Save();
        OnReady(p.S);
    }

    private void OnClosed(ShareSession s, string why)
    {
        Log.Write($"session {s.RemoteHost} closed: {why}");
        if (_pairing?.S == s) _pairing = null;
        if (_session != s) return;
        _session = null;
        if (_controlling) { _capture.End(0.5f); _controlling = false; }    // never leave this PC's input swallowed
        if (_controlled) { _emu.Leave(); _controlled = false; }
        _status = $"Disconnected ({why})";
        UpdateStatus(keepStatus: true);
    }

    private void OnMessage(ShareSession s, ShareMsg m)
    {
        if (m is ShareMsg.PairConfirm && _pairing?.S == s) { _pairing = (s, _pairing.Value.Local, true); FinishPairing(); return; }
        if (m is ShareMsg.PairReject && _pairing?.S == s) { s.Close("pairing declined on the other computer"); return; }
        if (s != _session) return;                         // input only from the paired, active session
        switch (m)
        {
            case ShareMsg.Enter e:
                if (_controlling) { _capture.End(null); _controlling = false; }
                if (e.Edge is 0 or 1 && e.Edge != _settings.MacEdge)
                {   // the Mac tells us which of our edges faces it (from its display arrangement)
                    _settings.MacEdge = e.Edge; _settings.Save(); _capture.Edge = _emu.Edge = e.Edge;
                }
                _emu.Enter(e.Position, takeover: e.Edge == 4); _controlled = true;
                Log.Write($"← the Mac is controlling this PC ({(e.Edge == 4 ? "take-over" : "came across")})");
                break;
            case ShareMsg.Leave l:
                if (_controlling) { _capture.End(_showsPc ? l.Position : null); _controlling = false; Log.Write("← back on this PC"); }
                else if (_controlled) { _emu.Leave(); _controlled = false; Log.Write("the Mac stopped controlling this PC"); }
                break;
            case ShareMsg.MonitorShows ms when Ddc.IsInput(ms.Code):
                MonitorNowShows(ms.Code); break;
            case ShareMsg.Move mv: if (_controlled) _emu.Move(mv.Dx, mv.Dy); break;
            case ShareMsg.Button b: if (_controlled) _emu.Button(b.Number, b.Down); break;
            case ShareMsg.Scroll sc: if (_controlled) _emu.Scroll(sc.Dx, sc.Dy); break;
            case ShareMsg.Key k: if (_controlled) _emu.Key(k.Usage, k.Down); break;
            case ShareMsg.ReleaseAll: _emu.ReleaseAll(); break;
            case ShareMsg.Clipboard c: ApplyClipboard(c.Text); break;
            case ShareMsg.MonitorPorts p when Ddc.IsInput(p.Mac) && Ddc.IsInput(p.Other) && p.Mac != p.Other:
                _settings.MacPort = p.Mac; _settings.PcPort = p.Other; _settings.Save(); ApplyMode(); break;
        }
        UpdateStatus();
    }

    // ---------------- edge hand-over ----------------

    private bool EdgeHit(float pos)
    {
        if (!_running || _session is null || _controlled || !_showsPc) return false;
        SendClipboardIfChanged();
        Log.Write("→ controlling the Mac (pointer crossed the edge)");
        _session.Send(new ShareMsg.Enter((byte)(1 - _settings.MacEdge), pos));   // enters the Mac's facing edge
        _controlling = true;
        UpdateStatus();
        return true;
    }

    private void PeerPointerLeft(float pos)
    {
        if (!_controlled || _session is null) return;
        _controlled = false;
        Log.Write("→ pointer reached the edge: back to the Mac");
        SendClipboardIfChanged();
        _session.Send(new ShareMsg.Leave((byte)_settings.MacEdge, pos));
        UpdateStatus();
    }

    private void SendClipboardIfChanged()
    {
        var seq = GetClipboardSequenceNumber();
        if (seq == _clipSeq) return;
        _clipSeq = seq;
        try
        {
            if (Clipboard.ContainsText() && Clipboard.GetText() is { Length: > 0 } t && System.Text.Encoding.UTF8.GetByteCount(t) <= Bhds.MaxClipboard)
                _session?.Send(new ShareMsg.Clipboard(t));
        }
        catch (ExternalException) { }                      // clipboard busy — skip this round
    }

    private void ApplyClipboard(string text)
    {
        try { if (Clipboard.ContainsText() && Clipboard.GetText() == text) { _clipSeq = GetClipboardSequenceNumber(); return; } Clipboard.SetText(text); }
        catch (ExternalException) { return; }
        _clipSeq = GetClipboardSequenceNumber();
    }

    // ---------------- UI ----------------

    private void UpdateStatus(bool keepStatus = false)
    {
        if (!keepStatus && _running)
        {
            var peer = _session?.Peer?.Name;
            _status = peer is not null
                ? _controlling ? $"Typing on {peer} — Ctrl+Alt+Win+Esc takes it back" : _controlled ? $"{peer}'s keyboard & mouse are on this PC" : $"Connected to {peer}"
                : _settings.Paired.Count == 0
                    ? (_unpaired.Count == 0 ? "Looking for BHDisplay on your Mac…" : "Found a computer — pair it from this menu")
                    : $"Waiting for {string.Join(", ", _settings.Paired.Values)}…";
        }
        var t = "BHDisplay — " + _status;
        _icon.Text = t.Length > 63 ? t[..63] : t;
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var cur = Ddc.CurrentInput();
        _menu.Items.Add(new ToolStripMenuItem($"{Ddc.MonitorName() ?? "Monitor"} — {(cur is { } c ? Ddc.NameOf(c) : "not found")}") { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem(cur == _settings.MacPort ? "Switch to this PC" : "Switch to the Mac", null, (_, _) => ToggleMonitor())
            { ShortcutKeyDisplayString = "Ctrl+Alt+Win+S" });
        int n = 2;
        foreach (var (code, name) in Ddc.Inputs)
        {
            var label = name + (code == _settings.MacPort ? "  ·  Mac" : code == _settings.PcPort ? "  ·  This PC" : "");
            var item = new ToolStripMenuItem(label, null, (_, _) => Switch(code)) { Checked = cur == code, ShortcutKeyDisplayString = $"Ctrl+Alt+Win+{n++ - 1}" };
            _menu.Items.Add(item);
        }
        _menu.Items.Add(new ToolStripSeparator());
        var share = new ToolStripMenuItem("Keyboard & Mouse Sharing", null, (_, _) =>
        {
            _settings.Sharing = !_settings.Sharing; _settings.Save();
            if (_settings.Sharing) StartSharing(); else StopSharing();
        }) { Checked = _settings.Sharing };
        _menu.Items.Add(share);
        if (_settings.Sharing)
        {
            _menu.Items.Add(new ToolStripMenuItem("    " + _status) { Enabled = false });
            _menu.Items.Add(new ToolStripMenuItem("    This PC: " + LocalAddresses()) { Enabled = false });
            if (_session is null)
                _menu.Items.Add(new ToolStripMenuItem("    Connect to the Mac by IP address…", null, (_, _) => AskForAddress()));
            foreach (var (_, (b, _)) in _unpaired)
                _menu.Items.Add(new ToolStripMenuItem($"    Pair with {b.Name}…", null, (_, _) => _ = Dial(b.Host, b.Port, () => { })));
            foreach (var (fp, name) in _settings.Paired.ToList())
                _menu.Items.Add(new ToolStripMenuItem($"    Forget {name}", null, (_, _) =>
                {
                    _settings.Paired.Remove(fp); _settings.PeerHosts.Remove(fp); _settings.Save();
                    if (_session is { } s && P256.Hex(s.PeerFingerprint) == fp) s.Close("forgotten");
                }));
        }
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Start with Windows", null, (_, _) => SetStartWithWindows(!StartsWithWindows())) { Checked = StartsWithWindows() });
        _menu.Items.Add(new ToolStripMenuItem("About BHDisplay", null, (_, _) => MessageBox.Show(
            $"BHDisplay for Windows {Application.ProductVersion}\n\nBuilt by BiswasHost — www.biswashost.com\nFree & open-source: github.com/wpexpertinbd/BHDisplay\n\n" +
            "Not affiliated with or endorsed by ViewSonic.", "About BHDisplay")));
        _menu.Items.Add(new ToolStripMenuItem("Quit BHDisplay", null, (_, _) => ExitThread()));
    }

    private void SetSide(int edge)
    {
        _settings.MacEdge = edge; _settings.Save();
        _capture.Edge = _emu.Edge = edge;
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static bool StartsWithWindows() => Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue("BHDisplay") is string;
    private static void SetStartWithWindows(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue("BHDisplay", $"\"{Application.ExecutablePath}\""); else k.DeleteValue("BHDisplay", false);
    }

    protected override void ExitThreadCore()
    {
        StopSharing();
        _hotkeys.Dispose();
        _icon.Visible = false; _icon.Dispose();
        base.ExitThreadCore();
    }
}

/// Hidden window that receives the global Ctrl+Alt+Win shortcuts.
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private readonly Action<int> _onHotkey;
    public HotkeyWindow(Action<int> onHotkey)
    {
        _onHotkey = onHotkey;
        CreateHandle(new CreateParams());
        const uint mods = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_WIN | Native.MOD_NOREPEAT;
        Native.RegisterHotKey(Handle, 1, mods, 'S');
        Native.RegisterHotKey(Handle, 2, mods, '1');
        Native.RegisterHotKey(Handle, 3, mods, '2');
        Native.RegisterHotKey(Handle, 4, mods, '3');
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY) _onHotkey((int)m.WParam);
        base.WndProc(ref m);
    }
    public void Dispose() { for (int i = 1; i <= 4; i++) Native.UnregisterHotKey(Handle, i); DestroyHandle(); }
}
