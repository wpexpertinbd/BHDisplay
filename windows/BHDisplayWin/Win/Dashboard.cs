// The BHDisplay window for Windows — the same controls as the Mac app's window: inputs, display controls, View
// Mode, colour temperature, monitor information, reset, and keyboard & mouse sharing. Closing it keeps the tray
// app running. All monitor traffic goes through one background DdcWorker; the UI never blocks on the monitor.
using System.Drawing;
using BHDisplay.Core;

namespace BHDisplay.Win;

/// What the window needs from the tray app.
internal interface IDashboardHost
{
    byte MacPort { get; }
    byte PcPort { get; }
    void SwitchInput(byte code);
    bool Sharing { get; set; }
    string SharingStatus { get; }
    IReadOnlyList<(string Fingerprint, string Name)> PairedComputers { get; }
    IReadOnlyList<(string Name, Action Pair)> FoundComputers { get; }
    bool Connected { get; }
    void PairNewComputer();
    void ConnectByAddress();
    void Forget(string fingerprint);
    double MacMouseSpeed { get; set; }
    double MacScrollSpeed { get; set; }
    bool StartWithWindows { get; set; }
    string SharedMonitorSerial { get; set; }
    bool TurnOffWhenMac { get; set; }
    bool SharedDisplayOff { get; }
    event Action? Changed;
}

internal sealed class Dashboard : Form
{
    private static readonly Color Bg = Color.FromArgb(243, 244, 247), Card = Color.White, Accent = Color.FromArgb(52, 78, 150),
        Muted = Color.FromArgb(110, 112, 120), Line = Color.FromArgb(226, 228, 233);
    private static readonly Font Body = new("Segoe UI", 9.75f), Title = new("Segoe UI Semibold", 12f), Big = new("Segoe UI Semibold", 15f),
        Small = new("Segoe UI", 8.25f);

    /// `-DocsScreenshot`: hide serial numbers for public screenshots (same switch as the Mac app).
    private static readonly bool DocsMode = Environment.GetCommandLineArgs().Contains("-DocsScreenshot");
    private static string Serial(string s) => DocsMode && s.Length > 0 && s != "—" ? "••••••••••••" : s;

    private readonly IDashboardHost _host;
    private readonly DdcWorker _ddc;
    private bool _loading;
    private Dictionary<byte, (uint Cur, uint Max)> _values = new();
    private MonitorDetails _details = new();

    // controls updated after a read
    private readonly Label _model = new(), _via = new(), _blueLock = new(), _status = new(), _pickNote = new();
    private readonly ComboBox _monitorPick = new();
    private readonly TableLayoutPanel _pickRow = new();
    private readonly CheckBox _isShared = new();
    private List<Ddc.MonitorEntry> _monitors = new();
    /// Monitor the window controls (null = the only one / the shared one).
    private string? _target;
    private bool TargetIsShared => _target is null || _target == _host.SharedMonitorSerial || (_monitors.Count <= 1 && _host.SharedMonitorSerial.Length == 0);
    private readonly Button[] _inputButtons = new Button[3];
    private readonly Button _switch = new();
    private readonly CheckBox _autoDetect = new(), _mute = new(), _startWithWindows = new(), _sharing = new(), _turnOff = new();
    private readonly Dictionary<byte, (TrackBar Bar, Label Value)> _sliders = new();
    private readonly ComboBox _viewMode = new(), _colorTemp = new();
    private readonly Stack _userColor = new() { BackColor = Card, Gap = 2 };
    private readonly Dictionary<string, Label> _info = new();
    private readonly FlowLayoutPanel _pairedList = new();
    private readonly TrackBar _mouseSpeed = new(), _scrollSpeed = new();
    private readonly Label _mouseSpeedValue = new(), _scrollSpeedValue = new();
    private readonly System.Windows.Forms.Timer _blueCheck = new() { Interval = 700 };
    private uint _blueWanted;

    public Dashboard(IDashboardHost host, SynchronizationContext ui)
    {
        _host = host;
        _ddc = new DdcWorker(ui);
        Text = "BHDisplay";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        BackColor = Bg; Font = Body;
        FormBorderStyle = FormBorderStyle.Sizable; MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(920, 760);
        MinimumSize = new Size(760, 520);
        AutoScroll = true;
        DoubleBuffered = true;

        _left = new Stack { BackColor = Bg, Gap = 12 }; _right = new Stack { BackColor = Bg, Gap = 12 };
        var left = _left; var right = _right;

        left.Controls.Add(HeaderCard());
        left.Controls.Add(InputCard());
        left.Controls.Add(InfoCard());
        right.Controls.Add(DisplayCard());
        right.Controls.Add(ChoiceCard("View Mode", _viewMode, Choices.ViewMode, Vcp.ViewMode, null));
        right.Controls.Add(ChoiceCard("Color Temperature", _colorTemp, Choices.ColorTemp, Vcp.ColorPreset, UserColorPanel()));
        right.Controls.Add(SharingCard());

        var footer = _footer;
        var credit = new LinkLabel { Text = $"BHDisplay v{Application.ProductVersion.Split('+')[0]} · Built by BiswasHost · Free & open-source",
            AutoSize = true, ForeColor = Muted, LinkColor = Accent, Font = Small, Dock = DockStyle.Left };
        credit.Links.Add(credit.Text.IndexOf("BiswasHost", StringComparison.Ordinal), "BiswasHost".Length, "https://www.biswashost.com/");
        credit.LinkClicked += (_, e) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo((string)e.Link.LinkData) { UseShellExecute = true });
        footer.Controls.Add(credit);
        var openLog = new LinkLabel { Text = "Check Log", AutoSize = true, LinkColor = Accent, Font = Small, Dock = DockStyle.Left, Padding = new Padding(12, 0, 0, 0), Visible = Log.Enabled };
        openLog.LinkClicked += (_, _) => TrayApp.OpenLog();
        footer.Controls.Add(openLog);
        footer.Controls.Add(new Label { Text = "Not affiliated with ViewSonic", AutoSize = true, ForeColor = Muted, Font = Small, Dock = DockStyle.Right });

        Controls.Add(_left); Controls.Add(_right); Controls.Add(_footer);

        _blueCheck.Tick += (_, _) => { _blueCheck.Stop(); CheckBlueLight(); };
        _host.Changed += OnHostChanged;
        Shown += (_, _) => Reload();
        FormClosed += (_, _) => { _host.Changed -= OnHostChanged; _ddc.Dispose(); _blueCheck.Dispose(); };
        RefreshSharing();
        NoMnemonics(this);
    }

    /// "&" is a keyboard-shortcut marker in WinForms text; show it literally ("Keyboard & Mouse").
    private static void NoMnemonics(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is Label l) l.UseMnemonic = false;
            if (c is ButtonBase b) b.UseMnemonic = false;
            NoMnemonics(c);
        }
    }

    // ---------------- layout helpers ----------------

    private readonly Stack _left, _right;
    private readonly Panel _footer = new() { Height = 30, BackColor = Bg };

    /// Lays its children out top to bottom at its own width (children tagged "fill" are stretched), and sizes its
    /// height to fit. Predictable on every DPI — no auto-size negotiation between nested panels.
    private sealed class Stack : Panel
    {
        public int Gap = 4;
        public bool Border;
        public Stack()
        {
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            int y = Padding.Top, inner = Width - Padding.Horizontal;
            foreach (Control c in Controls)
            {
                if (!c.Visible) continue;
                int avail = Math.Max(10, inner - c.Margin.Horizontal);
                if (Equals(c.Tag, "fill") || c is Stack) c.Width = avail;
                else
                {
                    // Measure everything else at the width actually available: text wraps instead of overflowing,
                    // buttons and rows get the size their content needs.
                    if (c is Label l) l.MaximumSize = new Size(avail, 0);
                    var pref = c.GetPreferredSize(new Size(avail, 0));
                    c.Size = new Size(Math.Min(pref.Width, avail), pref.Height);
                }
                c.Location = new Point(Padding.Left + c.Margin.Left, y + c.Margin.Top);
                if (c is Stack st) st.PerformLayout();
                y = c.Bottom + c.Margin.Bottom + Gap;
            }
            var h = y - Gap + Padding.Bottom;
            if (Height != h) Height = h;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Border) { using var p = new Pen(Line); e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1); }
        }
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_left is null) return;
        int pad = 16, colW = Math.Max(300, (ClientSize.Width - pad * 3) / 2);
        var o = AutoScrollPosition;
        _left.SetBounds(pad + o.X, 12 + o.Y, colW, _left.Height);
        _right.SetBounds(pad * 2 + colW + o.X, 12 + o.Y, colW, _right.Height);
        _left.PerformLayout(); _right.PerformLayout();
        _footer.SetBounds(pad + o.X, Math.Max(_left.Bottom, _right.Bottom) + 8, colW * 2 + pad, 30);
    }

    private Stack CardPanel(string title, out Stack body)
    {
        var card = new Stack { BackColor = Card, Padding = new Padding(16, 12, 16, 14), Border = true, Gap = 6 };
        if (title.Length > 0) card.Controls.Add(new Label { Text = title, Font = Title, AutoSize = true });
        card.ControlAdded += (_, _) => card.PerformLayout();
        body = card;
        return card;
    }

    private static Label Note(string text) => new() { Text = text, AutoSize = true, ForeColor = Muted, Font = Small, MaximumSize = Size.Empty };

    // ---------------- cards ----------------

    private Control HeaderCard()
    {
        var card = CardPanel("", out var body);
        _model.Font = Big; _model.AutoSize = true; _model.Text = "Monitor";
        _via.AutoSize = true; _via.ForeColor = Muted;
        body.Controls.Add(_model); body.Controls.Add(_via);
        // Several monitors on this PC: which one is also connected to the Mac?
        _pickNote.Text = "This PC has more than one monitor. Choose the one that is also connected to the Mac and tick the box below.";
        _pickNote.AutoSize = true; _pickNote.ForeColor = Color.FromArgb(170, 90, 0); _pickNote.Visible = false;
        _monitorPick.DropDownStyle = ComboBoxStyle.DropDownList; _monitorPick.Dock = DockStyle.Fill; _monitorPick.DropDownWidth = 460;
        _monitorPick.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || _monitorPick.SelectedIndex < 0 || _monitorPick.SelectedIndex >= _monitors.Count) return;
            _target = _monitors[_monitorPick.SelectedIndex].Key;     // which monitor this window controls
            Reload();
        };
        var identify = new Button { Text = "Identify", AutoSize = true, FlatStyle = FlatStyle.Flat, Anchor = AnchorStyles.Left, Margin = new Padding(8, 0, 0, 0) };
        identify.Click += (_, _) => Identify();
        _pickRow.ColumnCount = 2; _pickRow.Height = 32; _pickRow.Tag = "fill"; _pickRow.Visible = false; _pickRow.Margin = new Padding(0, 4, 0, 0);
        _pickRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); _pickRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _pickRow.Controls.Add(_monitorPick, 0, 0); _pickRow.Controls.Add(identify, 1, 0);
        _isShared.Text = "This is the monitor connected to the Mac (shortcuts and keyboard sharing use it)";
        _isShared.AutoSize = true; _isShared.Visible = false;
        _isShared.CheckedChanged += (_, _) =>
        {
            if (_loading || !_isShared.Checked || _target is null) return;
            if (!_target.StartsWith("sn:", StringComparison.Ordinal))
            {
                MessageBox.Show(this, "This monitor doesn't report a serial number, so BHDisplay can't recognise it reliably.", "BHDisplay");
                _loading = true; _isShared.Checked = false; _loading = false; return;
            }
            _host.SharedMonitorSerial = _target;
            Reload();
        };
        body.Controls.Add(_pickRow); body.Controls.Add(_isShared); body.Controls.Add(_pickNote);
        return card;
    }

    private Control InputCard()
    {
        var card = CardPanel("Input Selected", out var body);
        var row = new TableLayoutPanel { ColumnCount = Ddc.Inputs.Length, Height = 64, Margin = new Padding(0), Tag = "fill" };
        for (int i = 0; i < Ddc.Inputs.Length; i++)
        {
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / Ddc.Inputs.Length));
            var (code, name) = Ddc.Inputs[i];
            var b = new Button { Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI Semibold", 10f), Tag = code,
                Margin = new Padding(0, 0, i < Ddc.Inputs.Length - 1 ? 8 : 0, 0), UseMnemonic = false };
            b.FlatAppearance.BorderColor = Line;
            b.Click += (_, _) => SwitchTo(code);
            _inputButtons[i] = b; row.Controls.Add(b, i, 0);
        }
        body.Controls.Add(row);
        _switch.AutoSize = true; _switch.FlatStyle = FlatStyle.Flat; _switch.Margin = new Padding(0, 6, 0, 2); _switch.Padding = new Padding(10, 3, 10, 3);
        _switch.Click += (_, _) => SwitchTo(_values.TryGetValue(Vcp.Input, out var v) && (v.Cur & 0xFF) == _host.MacPort ? _host.PcPort : _host.MacPort);
        body.Controls.Add(_switch);
        _turnOff.Text = "Turn this monitor off in Windows while it shows the Mac (apps stay on your other screen)";
        _turnOff.AutoSize = true; _turnOff.Checked = _host.TurnOffWhenMac;
        _turnOff.CheckedChanged += (_, _) => { if (!_loading) _host.TurnOffWhenMac = _turnOff.Checked; };
        body.Controls.Add(_turnOff);
        _autoDetect.Text = "Auto Detect (the monitor scans for a live input by itself)"; _autoDetect.AutoSize = true;
        _autoDetect.CheckedChanged += (_, _) => { if (!_loading) _ddc.Write(Vcp.AutoDetect, _autoDetect.Checked ? 2u : 1u, _target); };
        body.Controls.Add(_autoDetect);
        body.Controls.Add(Note("Shortcuts:  Ctrl+Alt+Win+S  Mac ⇄ this PC     Ctrl+Alt+Win+1 / 2 / 3  DisplayPort / HDMI 1 / HDMI 2"));
        return card;
    }

    private Control InfoCard()
    {
        var card = CardPanel("Monitor information", out var body);
        var grid = new TableLayoutPanel { Height = 168, ColumnCount = 2, Margin = new Padding(0), Tag = "fill" };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        foreach (var k in new[] { "Serial Number", "Monitor Name", "Firmware Version", "Resolution", "PC Connection", "Manufactured" })
        {
            var cell = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Margin = new Padding(0, 0, 12, 8) };
            cell.Controls.Add(new Label { Text = k, ForeColor = Accent, AutoSize = true });
            var v = new Label { Text = "—", AutoSize = true, Font = new Font("Segoe UI", 10.5f) };
            _info[k] = v; cell.Controls.Add(v); grid.Controls.Add(cell);
        }
        body.Controls.Add(grid);
        var reset = new Button { Text = "Reset monitor to factory settings…", AutoSize = true, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 6, 0, 0), Padding = new Padding(10, 3, 10, 3) };
        reset.Click += (_, _) => FactoryReset();
        body.Controls.Add(reset);
        return card;
    }

    private Control DisplayCard()
    {
        var card = CardPanel("Display Control", out var body);
        foreach (var (code, name) in new[] { (Vcp.Brightness, "Brightness"), (Vcp.Contrast, "Contrast"), (Vcp.Sharpness, "Sharpness"),
                                             (Vcp.BlueLight, "Blue Light Filter"), (Vcp.Volume, "Volume") })
            body.Controls.Add(Slider(code, name));
        _blueLock.Text = "Locked by the monitor in this View Mode — switch View Mode to Standard to adjust.";
        _blueLock.AutoSize = true; _blueLock.ForeColor = Color.FromArgb(170, 90, 0); _blueLock.Font = Small; _blueLock.Visible = false;
        _blueLock.MaximumSize = Size.Empty;
        body.Controls.Add(_blueLock);
        _mute.Text = "Mute"; _mute.AutoSize = true;
        _mute.CheckedChanged += (_, _) => { if (!_loading) _ddc.Write(Vcp.Mute, _mute.Checked ? 1u : 2u, _target); };
        body.Controls.Add(_mute);
        return card;
    }

    private Control Slider(byte code, string name)
    {
        var row = new TableLayoutPanel { ColumnCount = 3, Height = 34, Margin = new Padding(0), Tag = "fill" };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        var bar = new TrackBar { Minimum = 0, Maximum = 100, TickStyle = TickStyle.None, Dock = DockStyle.Fill, AutoSize = false, Height = 30, Enabled = false, Margin = new Padding(0, 5, 0, 0) };
        var value = new Label { Text = "—", AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, Margin = new Padding(0) };
        bar.ValueChanged += (_, _) =>
        {
            value.Text = bar.Value.ToString();
            if (_loading) return;
            _ddc.Write(code, (uint)bar.Value, _target);
            if (code == Vcp.BlueLight) { _blueWanted = (uint)bar.Value; _blueCheck.Stop(); _blueCheck.Start(); }
        };
        row.Controls.Add(new Label { Text = name, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0) }, 0, 0);
        row.Controls.Add(bar, 1, 0); row.Controls.Add(value, 2, 0);
        _sliders[code] = (bar, value);
        return row;
    }

    private Control UserColorPanel()
    {
        _userColor.Visible = false;
        _userColor.Controls.Add(Slider(Vcp.Red, "Red")); _userColor.Controls.Add(Slider(Vcp.Green, "Green")); _userColor.Controls.Add(Slider(Vcp.Blue, "Blue"));
        return _userColor;
    }

    private Control ChoiceCard(string title, ComboBox combo, (uint Code, string Name)[] choices, byte code, Control? extra)
    {
        var card = CardPanel(title, out var body);
        combo.DropDownStyle = ComboBoxStyle.DropDownList; combo.Width = 200; combo.Enabled = false;
        foreach (var c in choices) combo.Items.Add(c.Name);
        combo.SelectedIndexChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0) return;
            var v = choices[combo.SelectedIndex].Code;
            if (code == Vcp.ColorPreset) _userColor.Visible = v == Choices.UserColor;
            if (_loading) return;
            _ddc.Write(code, v, _target);
            if (code == Vcp.ViewMode) CheckBlueLight();
        };
        body.Controls.Add(combo);
        if (extra is not null) body.Controls.Add(extra);
        return card;
    }

    private Control SharingCard()
    {
        var card = CardPanel("Keyboard & Mouse", out var body);
        _sharing.Text = "Share keyboard & mouse with the Mac"; _sharing.AutoSize = true;
        _sharing.CheckedChanged += (_, _) => { if (!_loading) _host.Sharing = _sharing.Checked; };
        body.Controls.Add(_sharing);
        _status.AutoSize = true; _status.ForeColor = Muted; _status.MaximumSize = Size.Empty; _status.Margin = new Padding(0, 4, 0, 6);
        body.Controls.Add(_status);
        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
        var pair = new Button { Text = "Pair a new computer…", AutoSize = true, FlatStyle = FlatStyle.Flat };
        pair.Click += (_, _) => _host.PairNewComputer();
        var byIp = new Button { Text = "Connect by IP address…", AutoSize = true, FlatStyle = FlatStyle.Flat };
        byIp.Click += (_, _) => _host.ConnectByAddress();
        buttons.Controls.Add(pair); buttons.Controls.Add(byIp);
        body.Controls.Add(buttons);
        _pairedList.AutoSize = true; _pairedList.FlowDirection = FlowDirection.TopDown; _pairedList.WrapContents = false; _pairedList.Margin = new Padding(0, 6, 0, 0);
        body.Controls.Add(_pairedList);
        body.Controls.Add(SpeedRow("Mouse speed", _mouseSpeed, _mouseSpeedValue, 5, 30, v => _host.MacMouseSpeed = v));
        body.Controls.Add(SpeedRow("Scroll speed", _scrollSpeed, _scrollSpeedValue, 5, 50, v => _host.MacScrollSpeed = v));
        body.Controls.Add(Note("Speed of the Mac's trackpad/mouse when it is on this PC. Moving the mouse never changes the monitor."));
        _startWithWindows.Text = "Start with Windows"; _startWithWindows.AutoSize = true; _startWithWindows.Margin = new Padding(0, 8, 0, 0);
        _startWithWindows.CheckedChanged += (_, _) => { if (!_loading) _host.StartWithWindows = _startWithWindows.Checked; };
        body.Controls.Add(_startWithWindows);
        return card;
    }

    private Control SpeedRow(string name, TrackBar bar, Label value, int min, int max, Action<double> set)
    {
        var row = new TableLayoutPanel { ColumnCount = 3, Height = 34, Margin = new Padding(0), Tag = "fill" };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        bar.Minimum = min; bar.Maximum = max; bar.TickStyle = TickStyle.None; bar.Dock = DockStyle.Fill; bar.AutoSize = false; bar.Height = 30; bar.Margin = new Padding(0, 5, 0, 0);
        value.AutoSize = false; value.Dock = DockStyle.Fill; value.TextAlign = ContentAlignment.MiddleRight; value.Margin = new Padding(0);
        bar.ValueChanged += (_, _) => { value.Text = (bar.Value / 10.0).ToString("0.0") + "×"; if (!_loading) set(bar.Value / 10.0); };
        row.Controls.Add(new Label { Text = name, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0) }, 0, 0);
        row.Controls.Add(bar, 1, 0); row.Controls.Add(value, 2, 0);
        return row;
    }

    // ---------------- data ----------------

    public void Reload()
    {
        _model.Text = "Reading the monitor…";
        var want = _target; var shared = _host.SharedMonitorSerial;
        _ddc.Do(() =>
        {
            var list = Ddc.ListMonitors();
            string? key = list.Count == 0 ? null
                : list.Any(m => m.Key == want) ? want : list.Any(m => m.Key == shared) ? shared : list[0].Key;
            var entry = list.FirstOrDefault(m => m.Key == key);
            return (Ddc.ReadMany(Vcp.Panel, key), MonitorDetails.Read(entry?.Serial ?? "", entry?.Device), list, key);
        }, r => { if (IsDisposed) return; _values = r.Item1; _details = r.Item2; _monitors = r.Item3; _target = r.Item4; Fill(); });
    }

    private void Merge(Dictionary<byte, (uint Cur, uint Max)> r) { foreach (var kv in r) _values[kv.Key] = kv.Value; }

    private void Fill()
    {
        _loading = true;
        try
        {
            bool found = _values.ContainsKey(Vcp.Input);
            _model.Text = found ? _details.Name : "Monitor not answering";
            _via.Text = found ? (TargetIsShared ? $"via {Ddc.NameOf(_host.PcPort)} · also connected to the Mac" : "connected to this PC only") : "Turn on Setup Menu ▸ DDC/CI ▸ On on the monitor, then reopen this window.";
            uint input = found ? _values[Vcp.Input].Cur & 0xFF : 0;
            foreach (var b in _inputButtons)
            {
                var code = (byte)b.Tag!;
                var tag = !TargetIsShared ? "" : code == _host.PcPort ? "\nThis PC" : code == _host.MacPort ? "\nMac" : "";
                b.Text = Ddc.NameOf(code) + tag;
                bool on = code == input;
                b.BackColor = on ? Accent : Color.FromArgb(240, 241, 244); b.ForeColor = on ? Color.White : Color.Black;
                b.Enabled = found;
            }
            _switch.Text = input == _host.MacPort ? "⇆  Switch to this PC" : "⇆  Switch to the Mac";
            _switch.Enabled = found; _switch.Visible = TargetIsShared;
            Set(_autoDetect, Vcp.AutoDetect, v => v == 2);
            Set(_mute, Vcp.Mute, v => v == 1);
            foreach (var kv in _sliders)
            {
                var (bar, value) = kv.Value;
                if (_values.TryGetValue(kv.Key, out var v))
                {
                    bar.Maximum = (int)Math.Max(1, Math.Min(v.Max == 0 ? 100 : v.Max, 1000));
                    bar.Value = (int)Math.Min(v.Cur, (uint)bar.Maximum); bar.Enabled = true; value.Text = bar.Value.ToString();
                }
                else { bar.Enabled = false; value.Text = "—"; }
            }
            Select(_viewMode, Choices.ViewMode, Vcp.ViewMode);
            Select(_colorTemp, Choices.ColorTemp, Vcp.ColorPreset);
            _userColor.Visible = _values.TryGetValue(Vcp.ColorPreset, out var cp) && (cp.Cur & 0xFF) == Choices.UserColor;
            // monitor choice (only when there is more than one)
            bool several = _monitors.Count > 1;
            _pickRow.Visible = several; _isShared.Visible = several;
            _monitorPick.Items.Clear();
            for (int i = 0; i < _monitors.Count; i++)
            {
                var m = _monitors[i];
                var side = _monitors.Count == 2 ? (i == 0 ? " (left)" : " (right)") : "";
                _monitorPick.Items.Add($"Monitor {i + 1}{side} · serial {(m.Serial.Length > 0 ? Serial(m.Serial) : "unknown")}" +
                                       (m.Key == _host.SharedMonitorSerial ? "  —  connected to the Mac" : ""));
            }
            int shown = _monitors.FindIndex(m => m.Key == _target);
            _monitorPick.SelectedIndex = several ? shown : -1;
            bool sharedChosen = _monitors.Any(m => m.Key == _host.SharedMonitorSerial);
            _pickNote.Visible = several && !sharedChosen && !_host.SharedDisplayOff;
            _turnOff.Visible = several || _host.SharedDisplayOff;
            if (_host.SharedDisplayOff)
                _via.Text = "The monitor connected to the Mac is showing the Mac — Windows' output to it is off until you switch back.";
            _isShared.Checked = several && TargetIsShared && sharedChosen;
            _isShared.Enabled = !_isShared.Checked;
            var target = shown >= 0 ? _monitors[shown] : null;
            _info["Serial Number"].Text = Serial(target?.Serial is { Length: > 0 } ss ? ss : _details.Serial);
            _info["Monitor Name"].Text = _details.Name;
            _info["Firmware Version"].Text = _values.TryGetValue(Vcp.Firmware, out var fw) ? $"{fw.Cur >> 8}.{fw.Cur & 0xFF:00}" : "—";
            _info["Resolution"].Text = _details.Resolution;
            _info["PC Connection"].Text = Ddc.NameOf(_host.PcPort);
            _info["Manufactured"].Text = _details.Manufactured;
        }
        finally { _loading = false; }
        RefreshSharing();
    }

    private void Set(CheckBox box, byte code, Func<uint, bool> on)
    {
        box.Enabled = _values.TryGetValue(code, out var v);
        if (box.Enabled) box.Checked = on(v.Cur & 0xFF);
    }

    private void Select(ComboBox combo, (uint Code, string Name)[] choices, byte code)
    {
        combo.Enabled = _values.TryGetValue(code, out var v);
        if (!combo.Enabled) return;
        int i = Array.FindIndex(choices, c => c.Code == (v.Cur & 0xFF));
        if (i < 0) { combo.SelectedIndex = -1; return; }
        combo.SelectedIndex = i;
    }

    /// The monitor ignores Blue Light Filter in some View Modes: read it back and say so instead of lying.
    private void CheckBlueLight()
    {
        var key = _target;
        _ddc.Do(() => Ddc.ReadMany(new[] { Vcp.BlueLight }, key), r =>
        {
            if (IsDisposed) return;
            Merge(r);
            if (!r.TryGetValue(Vcp.BlueLight, out var v)) return;
            bool locked = v.Cur != _blueWanted && _sliders[Vcp.BlueLight].Bar.Value == (int)_blueWanted;
            _blueLock.Visible = locked;
            if (locked) { _loading = true; try { _sliders[Vcp.BlueLight].Bar.Value = (int)Math.Min(v.Cur, (uint)_sliders[Vcp.BlueLight].Bar.Maximum); } finally { _loading = false; } }
        });
    }

    /// Shows a big number on each monitor for 3 s, matching the list.
    private void Identify()
    {
        for (int i = 0; i < _monitors.Count; i++)
        {
            var r = _monitors[i].Bounds;
            var f = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, TopMost = true, ShowInTaskbar = false,
                BackColor = Accent, Size = new Size(260, 220), Location = new Point(r.Left + 40, r.Top + 40) };
            f.Controls.Add(new Label { Text = (i + 1).ToString(), Dock = DockStyle.Fill, ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 96f), TextAlign = ContentAlignment.MiddleCenter });
            var t = new System.Windows.Forms.Timer { Interval = 3000 };
            t.Tick += (_, _) => { t.Stop(); t.Dispose(); f.Close(); };
            f.Show(); t.Start();
        }
    }

    private void SwitchTo(byte code)
    {
        if (TargetIsShared) _host.SwitchInput(code);
        else { var key = _target; _ddc.Do(() => Ddc.Switch(code, key), _ => { }); }
        var t = new System.Windows.Forms.Timer { Interval = 1500 };
        t.Tick += (_, _) => { t.Stop(); t.Dispose(); if (!IsDisposed) Reload(); };
        t.Start();
    }

    private void FactoryReset()
    {
        var name = _details.Name; var key = _target;          // decided BEFORE the dialog: the reset goes to the monitor it names
        if (key is null) return;
        if (MessageBox.Show(this, $"Reset {name} to its factory settings?\n\nBrightness, colours, View Mode and the other picture settings return to their defaults. " +
                "This can't be undone.", "BHDisplay", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        _ddc.Do(() => Ddc.FactoryReset(key), ok =>
        {
            if (IsDisposed) return;
            if (!ok) MessageBox.Show(this, "The monitor didn't accept the reset.", "BHDisplay");
            var t = new System.Windows.Forms.Timer { Interval = 3000 };
            t.Tick += (_, _) => { t.Stop(); t.Dispose(); if (!IsDisposed) Reload(); };
            t.Start();
        });
    }

    // ---------------- sharing ----------------

    private string _listSig = "";
    private long _lastRefresh;
    private bool _refreshQueued;
    /// Throttled: sharing raises Changed on every message while a mouse is in use.
    private void OnHostChanged()
    {
        if (!IsHandleCreated || IsDisposed || _refreshQueued) return;
        _refreshQueued = true;
        var wait = Math.Max(0, 300 - (Num.NowMs - _lastRefresh));
        var t = new System.Windows.Forms.Timer { Interval = (int)Math.Max(1, wait) };
        t.Tick += (_, _) => { t.Stop(); t.Dispose(); _refreshQueued = false; _lastRefresh = Num.NowMs; if (!IsDisposed) RefreshSharing(); };
        t.Start();
    }

    private void RefreshSharing()
    {
        bool was = _loading; _loading = true;
        try
        {
            _sharing.Checked = _host.Sharing;
            _status.Text = _host.Sharing ? _host.SharingStatus : "Off";
            _startWithWindows.Checked = _host.StartWithWindows;
            _mouseSpeed.Value = (int)Math.Round(Num.Clamp((float)_host.MacMouseSpeed, 0.5f, 3f) * 10);
            _scrollSpeed.Value = (int)Math.Round(Num.Clamp((float)_host.MacScrollSpeed, 0.5f, 5f) * 10);
            _mouseSpeedValue.Text = (_mouseSpeed.Value / 10.0).ToString("0.0") + "×";
            _scrollSpeedValue.Text = (_scrollSpeed.Value / 10.0).ToString("0.0") + "×";
        }
        finally { _loading = was; }

        var sig = string.Join("|", _host.FoundComputers.Select(f => "f:" + f.Name)) + "#" +
                  string.Join("|", _host.PairedComputers.Select(p => p.Fingerprint + p.Name)) + "#" + _host.Connected;
        if (sig == _listSig) return;                       // nothing changed: keep the rows (and any click in progress)
        _listSig = sig;
        _pairedList.SuspendLayout();
        while (_pairedList.Controls.Count > 0) _pairedList.Controls[0].Dispose();
        foreach (var (name, pair) in _host.FoundComputers)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            row.Controls.Add(new Label { Text = "🖥  " + name, AutoSize = true, Margin = new Padding(0, 7, 12, 0) });
            var b = new Button { Text = "Pair…", AutoSize = true, FlatStyle = FlatStyle.Flat };
            b.Click += (_, _) => pair();
            row.Controls.Add(b); _pairedList.Controls.Add(row);
        }
        foreach (var (fp, name) in _host.PairedComputers)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            row.Controls.Add(new Label { Text = "✔  " + name + (_host.Connected ? "  (connected)" : ""), AutoSize = true, Margin = new Padding(0, 7, 12, 0) });
            var b = new Button { Text = "Forget", AutoSize = true, FlatStyle = FlatStyle.Flat };
            b.Click += (_, _) =>
            {
                if (MessageBox.Show(this, $"Forget {name}? Its keyboard and mouse can't be used here until you pair again.", "BHDisplay",
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.OK)
                    _host.Forget(fp);
            };
            row.Controls.Add(b); _pairedList.Controls.Add(row);
        }
        NoMnemonics(_pairedList);
        _pairedList.ResumeLayout();
        _pairedList.Parent?.PerformLayout();
    }
}
