// BHDisplay for Windows — tray icon, monitor switching, and keyboard & mouse sharing with BHDisplay on the Mac.
// Moving the mouse never changes the monitor input; only the menu and Ctrl+Alt+Win+S / 1 / 2 / 3 do.
using System.Runtime.InteropServices;
using BHDisplay.Core;
using Microsoft.Win32;

namespace BHDisplay.Win;

internal sealed class TrayApp : ApplicationContext, IDashboardHost
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
    private readonly Dictionary<string, (Beacon B, DateTime Seen)> _unpaired = new();
    private readonly HashSet<string> _dialing = [];
    private DateTime _lastPrompt = DateTime.MinValue;
    private DateTime _pairArmedUntil = DateTime.MinValue;   // new pairings accepted only after the user asks, for 2 min
    private bool _promptOpen;                               // never stack pairing prompts

    private void ArmPairing()
    {
        _pairArmedUntil = DateTime.UtcNow.AddMinutes(2);
        Log.Write("ready to pair a new computer for 2 minutes");
        _status = "Ready to pair — choose “Pair” on the Mac now (2 minutes)";
        UpdateStatus(keepStatus: true);
    }
    private bool _running, _controlling, _controlled;
    private uint _clipSeq;
    private bool _showsPc;                                 // the shared monitor currently shows this PC
    private long _lastHandover;                            // no bouncing straight back across the boundary
    private bool RecentHandover => Num.NowMs - _lastHandover < 250;
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
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowDashboard(); };   // Benjamin: click = open the window
        _hotkeys = new HotkeyWindow(OnHotkey, OnSessionChange);
        Current = this;
        _clipSeq = GetClipboardSequenceNumber();

        _capture.Edge = _emu.Edge = _settings.MacEdge;
        _emu.Speed = _settings.MacMouseSpeed; _emu.ScrollSpeed = _settings.MacScrollSpeed;
        Ddc.PreferredSerial = _settings.SharedMonitorSerial ?? "";
        Log.Enabled = _settings.Logging;
        _capture.EdgeHit = pos => EdgeHit(pos);
        _capture.Captured = m => { if (_controlling) _session?.Send(m); };
        _capture.LocalHotkey = h => Post(() => OnLocalHotkey(h));   // never do slow work (DDC) inside the hook
        _emu.Left = pos => PeerPointerLeft(pos);
        _reconnect.Tick += (_, _) => ReconnectKnownPeers();

        Log.Write("tray icon created");
        // Everything else runs once the message loop is pumping: low-level hooks are serviced by that loop,
        // and nothing here may delay the tray icon from responding.
        _ui.Post(_ => Startup(), null);
        // An update or uninstall asks this copy to quit.
        _quit = new EventWaitHandle(false, EventResetMode.AutoReset, Installer.QuitEventName);
        new Thread(() => { if (_quit.WaitOne()) Post(ExitThread); }) { IsBackground = true }.Start();
    }
    private readonly EventWaitHandle _quit;

    private void Startup()
    {
        Log.Write("startup: message loop running");
        // Start with Windows only for the installed copy (a --portable copy in Downloads must never be auto-run).
        if (FirstRun() && Installer.IsInstalledCopy) { SetStartWithWindows(true); _settings.Save(); Log.Write("first run: start with Windows on"); }
        TurnSharedDisplayOn();                                   // never outlives a quit or crash
        if (_settings.DetachedDevice.Length == 0 && _settings.RestorePrimary.Length > 0)
        {   // the main display was moved but the monitor was never turned off (crash in between): give it back
            var dev = _settings.RestorePrimary;
            if (DisplayPower.IsOn(dev) && !DisplayPower.IsPrimary(dev) && DisplayPower.MakePrimary(dev)) Log.Write($"main display given back to {dev}");
            _settings.RestorePrimary = ""; _settings.Save();
        }
        if (_settings.Sharing) StartSharing();
        UpdateStatus();
        _icon.ShowBalloonTip(5000, "BHDisplay is running",
            "It lives in the system tray (click ^ next to the clock if you don't see it). Right-click the icon for options.", ToolTipIcon.Info);
        Log.Write("tray ready: " + _status);
        // Several monitors answer DDC and none is chosen yet: ask once which one is connected to the Mac.
        Task.Run(() => { Ddc.CurrentInput(); return Ddc.NeedsChoice; }).ContinueWith(t =>
        {
            if (t.Status == TaskStatus.RanToCompletion && t.Result)
                Post(() => _icon.ShowBalloonTip(8000, "BHDisplay — choose your monitor",
                    "This PC has more than one monitor. Click the BHDisplay icon and choose the one that is also connected to the Mac.", ToolTipIcon.Info));
        });
    }

    private void Post(Action a) => _ui.Post(_ => a(), null);

    private static bool FirstRun() => !File.Exists(Path.Combine(Settings.Dir, "settings.json"));

    // ---------------- monitor ----------------

    private void ToggleMonitor()
    {
        // Read the monitor off the UI thread (the input hooks live there). If it doesn't answer and our output to it
        // is off, it shows the Mac — so the toggle goes to this PC.
        Task.Run(() => Ddc.CurrentInput()).ContinueWith(t => Post(() =>
        {
            var cur = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
            if (cur is null && _settings.DetachedDevice.Length > 0) { Switch(_settings.PcPort); return; }
            Switch(cur == _settings.MacPort ? _settings.PcPort : _settings.MacPort);
        }));
    }

    private int _switchGen;                 // bumped by every switch: cancels a pending turn-off decided earlier
    private bool _switching;                // one local switch at a time
    private long _lastPeerRequest;

    private void Switch(byte code)
    {
        Interlocked.Increment(ref _switchGen);
        Log.Write($"switch to {Ddc.NameOf(code)} requested here (Mac port {Ddc.NameOf(_settings.MacPort)}, connected: {_session is not null})");
        // Going back to the Mac: let the Mac do it — it may have turned its output off while the monitor
        // showed this PC, and must turn it on before the monitor switches (or the monitor sees no signal).
        if (code == _settings.MacPort && _session is not null)
        {
            _session.Send(new ShareMsg.SwitchRequest(code));
            Log.Write($"asked the Mac to switch the monitor to {Ddc.NameOf(code)}");
            var asked = _session;
            _ = Task.Delay(10000).ContinueWith(_ => Post(() =>
            {   // no MONITOR_SHOWS answer: tell the user instead of failing silently
                if (_session == asked && _showsPc) { Log.Write("the Mac did not switch the monitor within 10 s");
                    _icon.ShowBalloonTip(4000, "BHDisplay", "The Mac didn't switch the monitor. Is BHDisplay running on the Mac?", ToolTipIcon.Warning); }
            }));
            return;
        }
        if (_switching) return;
        _switching = true;
        _offTimer?.Stop();
        bool turnOn = code == _settings.PcPort && _settings.DetachedDevice.Length > 0;
        int myGen = Volatile.Read(ref _switchGen);
        var det = (_settings.DetachedDevice, _settings.DetachedX, _settings.DetachedY, _settings.DetachedW, _settings.DetachedH, _settings.DetachedHz);
        // Everything slow (display mode change, DDC) runs off the UI thread so the keyboard/mouse hooks never stall.
        Task.Run(() =>
        {
            bool on = !turnOn || DisplayPower.TurnOn(det.Item1, det.Item2, det.Item3, det.Item4, det.Item5, det.Item6);
            if (turnOn && on) RestoreMainDisplay(det.Item1);
            if (turnOn && on)    // our output is back: wait until the monitor answers, THEN switch (no Auto Detect bounce)
                for (int i = 0; i < 30 && Ddc.CurrentInput() is null; i++) Thread.Sleep(200);
            bool ok = false;
            if (Volatile.Read(ref _switchGen) != myGen) return (on, ok: true);   // a newer choice was made meanwhile: it wins
            for (int i = 0; on && !ok && i < 3; i++) { ok = Ddc.Switch(code); if (!ok) Thread.Sleep(400); }   // the monitor sometimes ignores the first command
            return (on, ok);
        }).ContinueWith(t => Post(() =>
        {
            _switching = false;
            var (on, ok) = t.Status == TaskStatus.RanToCompletion ? t.Result : (false, false);
            if (turnOn) DisplayTurnedOn(on, det.Item1);
            if (!ok)
            {
                Log.Write($"switch to {Ddc.NameOf(code)} failed" + (turnOn ? $" (output on: {on}; {DisplayPower.LastError})" : ""));
                var why = turnOn ? "The monitor didn't come back — try again."
                    : Ddc.NeedsChoice ? "This PC has several monitors — click the BHDisplay icon and choose the one connected to the Mac."
                    : Ddc.PreferredSerial.Length > 0 ? "The monitor connected to the Mac isn't answering. Is it on, with Setup Menu ▸ DDC/CI ▸ On?"
                    : "Couldn't switch the monitor. Turn on Setup Menu ▸ DDC/CI on the monitor.";
                _icon.ShowBalloonTip(4000, "BHDisplay", why, ToolTipIcon.Warning);
                return;
            }
            if (code == _settings.MacPort)
                _icon.ShowBalloonTip(4000, "BHDisplay", "Switched without the Mac connected — if the Mac's output to the monitor is off, it shows no signal.", ToolTipIcon.Info);
            _session?.Send(new ShareMsg.MonitorShows(code));   // the Mac follows what the monitor shows
            MonitorNowShows(code);
        }));
    }

    /// "Input follows the monitor": shows the Mac → this PC's input goes to the Mac (take-over);
    /// shows this PC → keep input here, cross to the Mac at the edge facing it.
    private void MonitorNowShows(byte code)
    {
        bool showsPc = code == _settings.PcPort;
        bool changed = showsPc != _showsPc;
        if (changed) { _showsPc = showsPc; Log.Write($"monitor shows {(showsPc ? "this PC" : "the Mac")} ({Ddc.NameOf(code)})"); }
        Interlocked.Increment(ref _switchGen);
        if (code == _settings.MacPort) ScheduleTurnOff(); else { _offTimer?.Stop(); }
        ApplyMode(changed);
    }

    /// modeChanged: the monitor just changed computers. Only then is a crossed-over pointer brought back;
    /// a repeated "monitor shows" must not cancel a crossing made on purpose.
    private void ApplyMode(bool modeChanged = false)
    {
        if (!_running) return;
        // Each keyboard/mouse works on its own computer and crosses only at the edge, whatever the monitor shows.
        _capture.WatchEdge = true;
        if (modeChanged)
        {   // the screens moved under any crossed-over pointer: bring it home on both sides
            if (_controlling) StopControlling();
            if (_controlled)
            {   // tell the Mac, or it keeps swallowing its own keyboard/mouse
                _emu.Leave(); _controlled = false;
                _session?.Send(new ShareMsg.Leave(4, 0.5f));
                Log.Write("monitor changed: the Mac gets its keyboard/mouse back");
            }
        }
        UpdateStatus();
    }

    private void StopControlling()
    {
        _session?.Send(new ShareMsg.ReleaseAll());
        _session?.Send(new ShareMsg.Leave(4, 0.5f));
        _capture.End(null);
        _controlling = false;
        Log.Write("← stopped controlling the Mac");
    }

    public static TrayApp? Current { get; private set; }

    /// Opens BHDisplay's log in Notepad (for sending to support).
    public static void OpenLog()
    {
        try
        {
            if (!File.Exists(Log.FilePath)) Log.Write("log opened");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "notepad.exe"), "\"" + Log.FilePath + "\"") { UseShellExecute = false });
        }
        catch (Exception e) { MessageBox.Show("Couldn't open the log:\n" + e.Message + "\n\n" + Log.FilePath, "BHDisplay"); }
    }

    // ---------------- turning Windows' output to the shared monitor off while it shows the Mac ----------------

    private System.Windows.Forms.Timer? _offTimer;

    /// After the monitor has STAYED on the Mac for 6 s (never on an Auto Detect bounce), detach it from the desktop.
    private void ScheduleTurnOff()
    {
        if (!_settings.TurnOffWhenMac || _settings.DetachedDevice.Length > 0) return;
        _offTimer ??= new System.Windows.Forms.Timer { Interval = 6000 };
        _offTimer.Stop();
        _offTimer.Tick -= OffTick; _offTimer.Tick += OffTick;
        _offTimer.Start();
    }

    private void OffTick(object? sender, EventArgs e)
    {
        _offTimer?.Stop();
        int gen = _switchGen;
        Task.Run(() => (Ddc.FindShared(), Ddc.CurrentInput())).ContinueWith(t => Post(() =>
        {
            if (t.Status != TaskStatus.RanToCompletion) return;
            var (shared, input) = t.Result;
            // Anything happened since (a switch, the monitor now showing this PC)? Then this decision is stale.
            if (gen != _switchGen || _showsPc || _settings.DetachedDevice.Length > 0 || !_settings.TurnOffWhenMac) return;
            if (shared is null || input != _settings.MacPort)
            {
                Log.Write(shared is null ? "not turning the monitor off: can't tell which monitor is connected to the Mac"
                                         : $"not turning the monitor off: it shows {(input is { } i ? Ddc.NameOf(i) : "nothing readable")}");
                return;
            }
            var device = shared.Device;
            // All display changes off the UI thread (the keyboard/mouse hooks live there).
            Task.Run(() =>
            {
                bool movedMain = false;
                void GiveMainBack()                     // any abort after we moved the main display: put it back
                {
                    if (!movedMain) return;
                    bool back = DisplayPower.MakePrimary(device);
                    Log.Write(back ? $"main display given back to {device}" : $"couldn't give the main display back ({DisplayPower.LastError})");
                    if (back) _ui.Send(_ => { _settings.RestorePrimary = ""; _settings.Save(); }, null);
                }
                bool Wanted() => Volatile.Read(ref _switchGen) == gen;

                if (DisplayPower.ActiveCount() < 2) { Log.Write("not turning the monitor off: it is this PC's only screen"); return; }
                if (DisplayPower.IsPrimary(device))
                {
                    // Windows never turns off its main display (and opens apps there): make the other screen the main
                    // one for now — and remember to give the main display back when this monitor returns (Benjamin).
                    var other = DisplayPower.OtherDisplay(device);
                    if (other is null || !Wanted()) return;
                    _ui.Send(_ => { _settings.RestorePrimary = device; _settings.Save(); }, null);
                    movedMain = DisplayPower.MakePrimary(other);
                    Log.Write(movedMain ? $"made the other screen this PC's main display ({DisplayPower.LastError})"
                                        : $"couldn't make the other screen the main display ({DisplayPower.LastError})");
                    if (!movedMain) { _ui.Send(_ => { _settings.RestorePrimary = ""; _settings.Save(); }, null); return; }
                    Thread.Sleep(500);
                }
                var layout = DisplayPower.CurrentLayout(device);
                if (layout is not { } l) { Log.Write("not turning the monitor off: Windows still has it as the main display"); GiveMainBack(); return; }
                // Record BEFORE changing anything: a crash in between still gets the monitor back at the next start.
                bool stillWanted = false;
                _ui.Send(_ =>
                {
                    stillWanted = Wanted() && !_showsPc && _settings.DetachedDevice.Length == 0 && _settings.TurnOffWhenMac;
                    if (!stillWanted) return;
                    _settings.DetachedDevice = device;
                    (_settings.DetachedX, _settings.DetachedY, _settings.DetachedW, _settings.DetachedH, _settings.DetachedHz) = l;
                    _settings.Save();
                }, null);
                if (!stillWanted) { GiveMainBack(); return; }
                // Re-checked under the display lock: a switch back to this PC that started meanwhile wins.
                bool off = DisplayPower.TurnOff(device, Wanted);
                if (!off) GiveMainBack();
                Post(() =>
                {
                    if (off) Log.Write($"this PC's output to the monitor turned off ({device})");
                    else { Log.Write("not turning the monitor off after all (switched back, or Windows refused)"); if (_settings.DetachedDevice == device) { _settings.DetachedDevice = ""; _settings.Save(); } }
                    Changed?.Invoke();
                });
            });
        }));
    }

    /// Synchronous: only at start-up and quit (no keyboard/mouse forwarding in progress then).
    private void TurnSharedDisplayOn()
    {
        _offTimer?.Stop();
        if (_settings.DetachedDevice.Length == 0) return;
        var device = _settings.DetachedDevice;
        bool ok = DisplayPower.TurnOn(device, _settings.DetachedX, _settings.DetachedY, _settings.DetachedW, _settings.DetachedH, _settings.DetachedHz);
        if (ok && _settings.RestorePrimary == device)
        {
            Thread.Sleep(500);
            if (DisplayPower.MakePrimary(device)) { _settings.RestorePrimary = ""; _settings.Save(); Log.Write($"main display given back to {device}"); }
        }
        DisplayTurnedOn(ok, device);
    }

    /// Gives the main display back to the monitor that had it before we turned it off (background thread OK).
    private void RestoreMainDisplay(string device)
    {
        string want = "";
        _ui.Send(_ => want = _settings.RestorePrimary, null);
        if (want != device) return;
        Thread.Sleep(500);                                  // let Windows finish bringing the monitor back
        bool ok = DisplayPower.MakePrimary(device);
        Log.Write(ok ? $"main display given back to {device}" : $"couldn't give the main display back ({DisplayPower.LastError})");
        if (ok) _ui.Send(_ => { _settings.RestorePrimary = ""; _settings.Save(); }, null);
    }

    private void DisplayTurnedOn(bool ok, string device)
    {
        Log.Write(ok ? $"this PC's output to the monitor turned on ({device}; {DisplayPower.LastError})"
                     : $"couldn't turn this PC's output to the monitor back on ({DisplayPower.LastError})");
        // Done — or that display no longer exists at all (renumbered/removed): stop trying, never get stuck "off".
        if (ok || !DisplayPower.Exists(device)) { _settings.DetachedDevice = ""; _settings.Save(); }
        Changed?.Invoke();
    }

    bool IDashboardHost.TurnOffWhenMac
    {
        get => _settings.TurnOffWhenMac;
        set
        {
            _settings.TurnOffWhenMac = value; _settings.Save();
            if (!value && _settings.DetachedDevice.Length > 0)
            {
                var d = (_settings.DetachedDevice, _settings.DetachedX, _settings.DetachedY, _settings.DetachedW, _settings.DetachedH, _settings.DetachedHz);
                Task.Run(() => { var ok = DisplayPower.TurnOn(d.Item1, d.Item2, d.Item3, d.Item4, d.Item5, d.Item6); if (ok) RestoreMainDisplay(d.Item1); return ok; })
                    .ContinueWith(t => Post(() => DisplayTurnedOn(t.Status == TaskStatus.RanToCompletion && t.Result, d.Item1)));
            }
        }
    }
    bool IDashboardHost.SharedDisplayOff => _settings.DetachedDevice.Length > 0;

    // ---------------- the BHDisplay window ----------------

    private Dashboard? _dashboard;
    private void ShowDashboard()
    {
        if (_dashboard is null || _dashboard.IsDisposed)
        {
            _dashboard = new Dashboard(this, _ui);
            _dashboard.FormClosed += (_, _) => _dashboard = null;
            _dashboard.Show();
        }
        else { if (_dashboard.WindowState == FormWindowState.Minimized) _dashboard.WindowState = FormWindowState.Normal; _dashboard.Activate(); _dashboard.Reload(); }
    }

    public event Action? Changed;
    private string _changedSig = "";
    byte IDashboardHost.MacPort => _settings.MacPort;
    byte IDashboardHost.PcPort => _settings.PcPort;
    void IDashboardHost.SwitchInput(byte code) => Switch(code);
    bool IDashboardHost.Sharing
    {
        get => _settings.Sharing;
        set { if (_settings.Sharing == value) return; _settings.Sharing = value; _settings.Save(); if (value) StartSharing(); else StopSharing(); UpdateStatus(); }
    }
    string IDashboardHost.SharingStatus => _status;
    IReadOnlyList<(string Fingerprint, string Name)> IDashboardHost.PairedComputers => _settings.Paired.Select(kv => (kv.Key, kv.Value)).ToList();
    IReadOnlyList<(string Name, Action Pair)> IDashboardHost.FoundComputers =>
        _unpaired.Values.Select(v => (v.B.Name, (Action)(() => { ArmPairing(); _ = Dial(v.B.Host, Bhds.TcpPort, () => { }); }))).ToList();
    bool IDashboardHost.Connected => _session is not null;
    void IDashboardHost.PairNewComputer() => ArmPairing();
    void IDashboardHost.ConnectByAddress() => AskForAddress();
    void IDashboardHost.Forget(string fp) => Forget(fp);
    double IDashboardHost.MacMouseSpeed { get => _settings.MacMouseSpeed; set { _settings.MacMouseSpeed = value; _emu.Speed = value; _settings.Save(); } }
    double IDashboardHost.MacScrollSpeed { get => _settings.MacScrollSpeed; set { _settings.MacScrollSpeed = value; _emu.ScrollSpeed = value; _settings.Save(); } }
    bool IDashboardHost.StartWithWindows { get => StartsWithWindows(); set => SetStartWithWindows(value); }
    string IDashboardHost.SharedMonitorSerial
    {
        get => _settings.SharedMonitorSerial ?? "";
        set { _settings.SharedMonitorSerial = value; Ddc.PreferredSerial = value; _settings.Save(); Log.Write("monitor connected to the Mac: serial " + value); }
    }

    private void Forget(string fp)
    {
        _settings.Paired.Remove(fp); _settings.PeerHosts.Remove(fp); _settings.Save();
        if (_session is { } s && P256.Hex(s.PeerFingerprint) == fp) s.Close("forgotten");
        UpdateStatus();
    }

    /// The Mac's keyboard/mouse is in use here: make sure the display is on (and stays on), like real input would.
    private long _lastActive;
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
    private void UserIsActive()
    {
        if (Num.NowMs - _lastActive < 2000) return;
        _lastActive = Num.NowMs;
        SetThreadExecutionState(0x00000002 | 0x00000001);   // ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED (one-shot reset)
    }

    /// Before a crash dialog: stop swallowing this PC's input and release keys pressed for the Mac,
    /// or nothing could click the dialog's OK.
    public static void EmergencyRelease()
    {
        var t = Current;
        if (t is null) return;
        try { t._capture.Dispose(); } catch { }
        try { t._emu.ReleaseAll(); } catch { }
    }

    /// Locked, Ctrl+Alt+Del, UAC or switched user: the hooks stop seeing keys (key-ups go to the secure desktop),
    /// so hand everything back on both sides. On unlock, re-install the hooks (Windows may have dropped them).
    private void OnSessionChange(int code)
    {
        if (code is Native.WTS_SESSION_LOCK or Native.WTS_CONSOLE_DISCONNECT or Native.WTS_REMOTE_DISCONNECT)
        {
            Log.Write("session locked or disconnected: giving keyboard/mouse back on both sides");
            if (_controlling) StopControlling();
            if (_controlled) { _emu.Leave(); _controlled = false; _session?.Send(new ShareMsg.Leave(4, 0.5f)); }
            UpdateStatus();
        }
        else if (code == Native.WTS_SESSION_UNLOCK && _running)
        {
            _capture.Restart();
            Log.Write("session unlocked: input hooks re-installed");
        }
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
            case 0x29: StopControlling(); UpdateStatus(); break;   // Esc: take this PC's input back
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
            _id = ShareIdentity.LoadOrCreate(Settings.LoadIdentity, Settings.SaveIdentity, Environment.MachineName, Log.Write);
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
        bool meLower = Bytes.Compare(_id.DeviceId, s.Peer.DeviceId) < 0;
        return (s.Role == Role.Dialer) == meLower;
    }

    private void AskForAddress()
    {
        using var f = new Form { Text = "BHDisplay — connect to the Mac", Width = 380, Height = 160, FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen, MaximizeBox = false, MinimizeBox = false, TopMost = true };
        var label = new Label { Text = "IP address of the Mac running BHDisplay (e.g. 192.168.0.123):", Left = 12, Top = 14, Width = 340 };
        var box = new TextBox { Left = 12, Top = 38, Width = 340 };
        var ok = new Button { Text = "Connect", Left = 196, Top = 72, Width = 75, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 277, Top = 72, Width = 75, DialogResult = DialogResult.Cancel };
        f.Controls.AddRange([label, box, ok, cancel]); f.AcceptButton = ok; f.CancelButton = cancel;
        if (f.ShowDialog() != DialogResult.OK) return;
        if (!System.Net.IPAddress.TryParse(box.Text.Trim(), out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        { MessageBox.Show("That isn't an IPv4 address.", "BHDisplay"); return; }
        ArmPairing();
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
        _settings.Paired.Keys.Any(k => Bytes.FromHex(k) is { Length: 32 } f && Bytes.Equal(Bytes.Slice(f, 0, 8), prefix));

    private readonly HashSet<string> _loggedBeacons = [];
    private void OnBeacon(Beacon b)
    {
        if (!_running || _id is null) return;
        var key = P256.Hex(b.DeviceId);
        if (_loggedBeacons.Count > 64) _loggedBeacons.Clear();     // bounded: beacons are unauthenticated
        if (_loggedBeacons.Add(key)) Log.Write($"discovered {b.Name} at {b.Host}:{b.Port} (paired: {IsPaired(b.FingerprintPrefix)})");
        if (IsPaired(b.FingerprintPrefix))
        {
            _unpaired.Remove(key);
            // Lower device id dials; the other side waits for it.
            // Keyed by address, only our own port, a few at a time: a forged beacon can't make us dial around.
            if (_session is null && Bytes.Compare(_id.DeviceId, b.DeviceId) < 0 && _dialing.Count < 4 && _dialing.Add(b.Host))
            {
                _ = Dial(b.Host, Bhds.TcpPort, () => _dialing.Remove(b.Host));
            }
        }
        else if (_unpaired.ContainsKey(key) || _unpaired.Count < 16) _unpaired[key] = (b, DateTime.UtcNow);
        foreach (var k in _unpaired.Where(kv => DateTime.UtcNow - kv.Value.Seen > TimeSpan.FromSeconds(10)).Select(kv => kv.Key).ToList())
            _unpaired.Remove(k);
    }

    private async Task Dial(string host, int port, Action done)
    {
        try { var s = await ShareSession.DialAsync(host, port, _id!); Post(() => Adopt(s)); }
        catch { }
        finally { _ = Task.Delay(15000).ContinueWith(_ => Post(done)); }
    }

    private readonly HashSet<ShareSession> _handshaking = [];

    private void Adopt(ShareSession s)
    {
        // At most a few handshakes at a time: strangers can't pile up connections.
        if (_handshaking.Count >= 8) { s.Close("too many connections"); return; }
        _handshaking.Add(s);
        s.Ready += x => Post(() => { _handshaking.Remove(x); OnReady(x); });
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
                // The old session's close callback will find it is no longer current and do nothing,
                // so release everything it carried now — never keep capturing for a session that is gone.
                if (_controlling) { _capture.End(null); _controlling = false; }
                if (_controlled) { _emu.Leave(); _controlled = false; }
                old.Close("duplicate connection");
            }
            _session = s;
            _settings.PeerHosts[fp] = s.RemoteHost; _settings.Save();
            ApplyMode();
            UpdateStatus();
            return;
        }
        // Unpaired: only while the user asked for a pairing on this PC; one at a time, at most one prompt every 5 s.
        if (DateTime.UtcNow >= _pairArmedUntil) { s.Close("not accepting new pairings"); return; }
        if (_pairing is not null || _promptOpen || DateTime.UtcNow - _lastPrompt < TimeSpan.FromSeconds(5)) { s.Close("busy pairing"); return; }
        _lastPrompt = DateTime.UtcNow;
        _pairing = (s, false, false);
        _ = Task.Delay(60000).ContinueWith(_ => Post(() => { if (_pairing?.S == s) s.Close("pairing timed out"); }));
        var name = s.Peer?.Name ?? "another computer";
        var code = s.PairCode.Substring(0, 3) + " " + s.PairCode.Substring(3);
        var sameName = _settings.Paired.ContainsValue(name)
            ? $"\n\n⚠ A computer named “{name}” is already paired — this is a DIFFERENT computer using that name." : "";
        _promptOpen = true;
        bool answer;
        try
        {
            answer = PairDialog.Ask($"Pair with “{name}”?",
                $"{name} ({s.RemoteHost}) wants to share keyboard and mouse with this PC.\n\nPairing code:  {code}\n\n" +
                "Pair only if the other computer shows exactly the same code. Once paired, its keyboard and mouse can control this PC." + sameName);
        }
        finally { _promptOpen = false; }
        if (_pairing?.S != s) return;                      // timed out or closed while the dialog was open
        if (answer)
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
        _pairArmedUntil = DateTime.MinValue;
        _settings.Paired[P256.Hex(p.S.PeerFingerprint)] = p.S.Peer?.Name ?? "Mac";
        _settings.Save();
        OnReady(p.S);
    }

    private void OnClosed(ShareSession s, string why)
    {
        _handshaking.Remove(s);
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
        if (s != _session)                                 // input only from the paired, active session
        {
            if (m is not (ShareMsg.Ping or ShareMsg.Pong)) Log.Write($"ignored {m.GetType().Name} from a connection that is not the active one");
            return;
        }
        switch (m)
        {
            case ShareMsg.Enter e:
                if (_controlling) { _capture.End(null); _controlling = false; }
                if (e.Edge is 0 or 1 && e.Edge != _settings.MacEdge)
                {   // the Mac tells us which of our edges faces it (from its display arrangement)
                    _settings.MacEdge = e.Edge; _settings.Save(); _capture.Edge = _emu.Edge = e.Edge;
                }
                _emu.Enter(e.Position, takeover: e.Edge == 4); _controlled = true; _lastHandover = Num.NowMs;
                Log.Write($"← the Mac is controlling this PC ({(e.Edge == 4 ? "take-over" : "came across")})");
                break;
            case ShareMsg.Leave l:
                if (_controlling) { _capture.End(l.Position); _controlling = false; Log.Write("← back on this PC"); }
                else if (_controlled) { _emu.Leave(); _controlled = false; Log.Write("the Mac stopped controlling this PC"); }
                break;
            case ShareMsg.SwitchRequest q when q.Code == _settings.PcPort:
                // Only switches to THIS PC's input come here (we may need to turn our output on first).
                s.Send(new ShareMsg.SwitchAccepted(q.Code));          // "I'm doing it" — the Mac must not switch itself
                if (_switching || Num.NowMs - _lastPeerRequest < 1000) break;   // already on it
                _lastPeerRequest = Num.NowMs;
                Log.Write($"the Mac asks to switch the monitor to {Ddc.NameOf(q.Code)}");
                Switch(q.Code); break;
            case ShareMsg.MonitorShows ms when Ddc.IsInput(ms.Code):
                Log.Write($"the Mac says the monitor shows {Ddc.NameOf(ms.Code)}");
                MonitorNowShows(ms.Code); break;
            case ShareMsg.Move mv: if (_controlled) { UserIsActive(); _emu.Move(mv.Dx, mv.Dy); } break;
            case ShareMsg.Button b: if (_controlled) _emu.Button(b.Number, b.Down); break;
            case ShareMsg.Scroll sc: if (_controlled) _emu.Scroll(sc.Dx, sc.Dy); break;
            case ShareMsg.Key k: if (_controlled) { UserIsActive(); _emu.Key(k.Usage, k.Down); } break;
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
        if (!_running || _session is null || RecentHandover) return false;
        _lastHandover = Num.NowMs;
        if (_controlled) { _emu.Leave(); _controlled = false; }   // our own mouse wins: the Mac's pointer was here
        Post(SendClipboardIfChanged);                       // clipboard can block: not inside the hook
        Log.Write("→ controlling the Mac (pointer crossed the edge)");
        _session.Send(new ShareMsg.Enter((byte)(1 - _settings.MacEdge), pos));   // enters the Mac's facing edge
        _controlling = true;
        UpdateStatus();
        return true;
    }

    private void PeerPointerLeft(float pos)
    {
        // No time guard here: the emulator has already let go, so the Mac MUST be told, or it keeps sending into nothing.
        if (!_controlled || _session is null) return;
        _controlled = false;
        _lastHandover = Num.NowMs;
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
            // Password managers mark secrets with these formats (Microsoft's clipboard-history convention): never send them.
            if (Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing") || Clipboard.ContainsData("CanIncludeInClipboardHistory")
                && Clipboard.GetDataObject()?.GetData("CanIncludeInClipboardHistory") is System.IO.MemoryStream { Length: >= 4 } ms && BitConverter.ToInt32(ms.ToArray(), 0) == 0)
                return;
            if (Clipboard.ContainsText() && Clipboard.GetText() is { Length: > 0 } t && System.Text.Encoding.UTF8.GetByteCount(t) <= Bhds.MaxClipboard)
                _session?.Send(new ShareMsg.Clipboard(t));
        }
        catch (ExternalException) { }                      // clipboard busy — skip this round
    }

    private void ApplyClipboard(string text)
    {
        try
        {
            if (Clipboard.ContainsText() && Clipboard.GetText() == text) { _clipSeq = GetClipboardSequenceNumber(); return; }
            Clipboard.SetText(text);           // secrets never arrive: the Mac skips concealed (password) items
        }
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
        _icon.Text = t.Length > 63 ? t.Substring(0, 63) : t;
        // Only when something visible changed (this runs for every message while a mouse is in use).
        var sig = $"{_status}|{_settings.Sharing}|{_settings.Paired.Count}|{_unpaired.Count}|{_session is not null}";
        if (sig != _changedSig) { _changedSig = sig; Changed?.Invoke(); }
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        _menu.Items.Add(new ToolStripMenuItem("Open BHDisplay…", null, (_, _) => ShowDashboard()) { Font = new Font(_menu.Font, FontStyle.Bold) });
        _menu.Items.Add(new ToolStripSeparator());
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
        var share = new ToolStripMenuItem("Keyboard && Mouse Sharing", null, (_, _) =>
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
            _menu.Items.Add(new ToolStripMenuItem("    Pair a new computer…", null, (_, _) => ArmPairing()));
            foreach (var (_, (b, _)) in _unpaired)
                _menu.Items.Add(new ToolStripMenuItem($"    Pair with {b.Name}…", null, (_, _) => { ArmPairing(); _ = Dial(b.Host, Bhds.TcpPort, () => { }); }));
            foreach (var (fp, name) in _settings.Paired.ToList())
                _menu.Items.Add(new ToolStripMenuItem($"    Forget {name}", null, (_, _) => Forget(fp)));
        }
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Start with Windows", null, (_, _) => SetStartWithWindows(!StartsWithWindows())) { Checked = StartsWithWindows() });
        _menu.Items.Add(new ToolStripMenuItem("Keep a Log", null, (_, _) =>
        {
            if (Log.Enabled) Log.Write("log turned off");
            Log.Enabled = _settings.Logging = !Log.Enabled; _settings.Save();
            if (Log.Enabled) Log.Write("log turned on");
            Changed?.Invoke();
        }) { Checked = _settings.Logging });
        _menu.Items.Add(new ToolStripMenuItem("Check Log", null, (_, _) => OpenLog()) { Enabled = _settings.Logging });
        var age = new ToolStripMenuItem("Delete Log Entries Older Than") { Enabled = _settings.Logging };
        foreach (var d in new[] { 3, 7 })
            age.DropDownItems.Add(new ToolStripMenuItem($"{d} Days", null, (_, _) =>
            {
                _settings.LogKeepDays = d; _settings.Save(); Log.KeepDays = d; Log.Prune(force: true);
            }) { Checked = (_settings.LogKeepDays == 3 ? 3 : 7) == d });
        _menu.Items.Add(age);
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
        StopSharing();                                           // release the keyboard/mouse hooks first
        TurnSharedDisplayOn();                                   // never leave the shared monitor off in Windows
        _hotkeys.Dispose();
        _icon.Visible = false; _icon.Dispose();
        base.ExitThreadCore();
    }
}

/// Hidden window that receives the global Ctrl+Alt+Win shortcuts.
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private readonly Action<int> _onHotkey, _onSession;
    public HotkeyWindow(Action<int> onHotkey, Action<int> onSession)
    {
        _onHotkey = onHotkey; _onSession = onSession;
        CreateHandle(new CreateParams());
        Native.WTSRegisterSessionNotification(Handle, 0);   // NOTIFY_FOR_THIS_SESSION
        const uint mods = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_WIN | Native.MOD_NOREPEAT;
        Native.RegisterHotKey(Handle, 1, mods, 'S');
        Native.RegisterHotKey(Handle, 2, mods, '1');
        Native.RegisterHotKey(Handle, 3, mods, '2');
        Native.RegisterHotKey(Handle, 4, mods, '3');
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY) _onHotkey((int)m.WParam);
        else if (m.Msg == Native.WM_WTSSESSION_CHANGE) _onSession((int)m.WParam);
        base.WndProc(ref m);
    }
    public void Dispose() { for (int i = 1; i <= 4; i++) Native.UnregisterHotKey(Handle, i); Native.WTSUnRegisterSessionNotification(Handle); DestroyHandle(); }
}

/// Pairing confirmation: no key answers "Pair" (a stray Enter or "y" must never pair) and the button can't be
/// clicked for the first 1.5 s. Esc = Don't Pair.
internal static class PairDialog
{
    public static bool Ask(string title, string text)
    {
        using var f = new Form { Text = "BHDisplay — " + title, Width = 460, Height = 270, FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen, MaximizeBox = false, MinimizeBox = false, TopMost = true, ShowInTaskbar = true };
        var label = new Label { Text = text, Left = 14, Top = 12, Width = 420, Height = 170, UseMnemonic = false };
        var pair = new Button { Text = "Pair", Left = 250, Top = 190, Width = 85, Enabled = false, UseMnemonic = false };
        var no = new Button { Text = "Don't Pair", Left = 345, Top = 190, Width = 90, DialogResult = DialogResult.Cancel, UseMnemonic = false };
        pair.Click += (_, _) => { f.DialogResult = DialogResult.OK; f.Close(); };
        f.Controls.AddRange([label, pair, no]);
        f.CancelButton = no;
        f.Shown += (_, _) => { no.Focus(); };
        var t = new System.Windows.Forms.Timer { Interval = 1500 };
        t.Tick += (_, _) => { t.Stop(); pair.Enabled = true; };
        t.Start();
        var r = f.ShowDialog();
        t.Dispose();
        return r == DialogResult.OK;
    }
}
