using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace LoadView
{
    // Category-sidebar settings dialog with instant live preview. Every change is pushed to
    // the overlay via the preview callback; OK keeps it (caller persists), Cancel reverts.
    internal sealed class SettingsForm : Form
    {
        private static readonly Color Bg = Color.FromArgb(32, 32, 36);
        private static readonly Color NavBg = Color.FromArgb(24, 24, 28);
        private static readonly Color Ink = Color.FromArgb(232, 232, 237);
        private static readonly Color Dim = Color.FromArgb(150, 150, 158);
        private static readonly Color Accent = Color.FromArgb(0x6F, 0xA8, 0xFF);
        private static readonly Color FieldBg = Color.FromArgb(46, 46, 52);

        private Settings _working;
        private readonly Action<Settings> _preview;
        private bool _suspend;

        private ListBox _nav;
        private Panel _host;
        private readonly List<Panel> _pages = new List<Panel>();
        private readonly ToolTip _tips = new ToolTip();

        // layout metrics (within a page)
        private Panel _panel;
        private int _y;
        private const int LabelX = 16;
        private const int LabelW = 176;
        private const int CtrlX = 200;

        // controls
        private NumericUpDown _width, _graphH, _driveH, _refreshMs, _clockSize, _dateSize, _daySize,
            _driveLblSize, _listSize, _ipSize, _netTotalsSize, _ipLanSec, _ipWanSec;
        private CheckBox _seconds, _dateBold, _dayBold, _driveLblBold, _netBytes, _extIp, _top, _lock, _startup, _debugLog;
        private Button _clockColor, _dateColor, _dayColor, _netDownColor, _netUpColor;
        private CheckedListBox _order;
        private TrackBar _opacity;
        private Label _opacityVal;
        private readonly Button[] _gColor = new Button[5];
        private readonly NumericUpDown[] _gMax = new NumericUpDown[5];
        private readonly NumericUpDown[] _gAlert = new NumericUpDown[5];

        public Settings Result { get { return _working; } }

        public SettingsForm(Settings working, Action<Settings> preview)
        {
            _working = working;
            _preview = preview;

            Text = "LoadView Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Bg;
            ForeColor = Ink;
            Font = new Font("Segoe UI", 9.5f);
            AutoScaleMode = AutoScaleMode.Font; // scale consistently on high-DPI displays
            ClientSize = new Size(660, 560);

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 52;
            bottom.BackColor = Bg;
            Controls.Add(bottom);
            BuildButtons(bottom);

            _host = new Panel();
            _host.Dock = DockStyle.Fill;
            _host.BackColor = Bg;
            Controls.Add(_host);

            _nav = new ListBox();
            _nav.Dock = DockStyle.Left;
            _nav.Width = 150;
            _nav.BackColor = NavBg;
            _nav.ForeColor = Ink;
            _nav.BorderStyle = BorderStyle.None;
            _nav.IntegralHeight = false; // fill the docked height (don't collapse to whole items)
            _nav.DrawMode = DrawMode.OwnerDrawFixed;
            _nav.ItemHeight = 30;
            _nav.DrawItem += NavDrawItem;
            _nav.SelectedIndexChanged += delegate { ShowPage(_nav.SelectedIndex); };
            Controls.Add(_nav);
            _host.BringToFront(); // Fill must be front of z-order so it docks beside the nav, not under it

            _suspend = true;
            BuildAllPages();
            _suspend = false;

            _nav.SelectedIndex = 0;
        }

        // ---------- pages ----------

        private void BuildAllPages()
        {
            AddPage("Layout", BuildLayout);
            AddPage("Sections", BuildSections);
            AddPage("Graphs", BuildGraphs);
            AddPage("Clock & date", BuildClockDate);
            AddPage("Drives & lists", BuildDrivesLists);
            AddPage("Network", BuildNetwork);
            AddPage("Behavior", BuildBehavior);
            AddPage("Defaults", BuildDefaults);
        }

        private void AddPage(string name, Action build)
        {
            Panel p = new Panel();
            p.BackColor = Bg;
            p.Dock = DockStyle.Fill;
            p.AutoScroll = true;
            p.Visible = false;
            _host.Controls.Add(p);
            _panel = p;
            _y = 16;
            build();
            _pages.Add(p);
            _nav.Items.Add(name);
        }

        private void ShowPage(int index)
        {
            for (int i = 0; i < _pages.Count; i++) _pages[i].Visible = (i == index);
            if (index >= 0 && index < _pages.Count) _pages[index].BringToFront();
        }

        private void NavDrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (SolidBrush b = new SolidBrush(sel ? Accent : NavBg))
                e.Graphics.FillRectangle(b, e.Bounds);
            Rectangle t = new Rectangle(e.Bounds.X + 12, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, _nav.Items[e.Index].ToString(), _nav.Font, t,
                sel ? Color.White : Ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        private void BuildLayout()
        {
            _width   = AddNum("Window width (px)", 180, 800, _working.Width, "Overall panel width.");
            _graphH  = AddNum("Graph height (px)", 24, 400, _working.GraphHeight, "Height of each metric graph.");
            _driveH  = AddNum("Drive bar height (px)", 16, 160, _working.DriveRowHeight, "Height of each drive row.");
            _refreshMs = AddNum("Refresh interval (ms)", 200, 10000, _working.RefreshMs, "How often the metrics update.");
            _refreshMs.Increment = 100;
        }

        private void BuildSections()
        {
            Hint("Check = visible.  Select an item and use ▲ ▼ to reorder.");
            _order = new CheckedListBox();
            _order.BackColor = FieldBg;
            _order.ForeColor = Ink;
            _order.BorderStyle = BorderStyle.FixedSingle;
            _order.IntegralHeight = false;
            _order.CheckOnClick = true;
            _order.SetBounds(LabelX, _y, 300, Settings.AllSections.Length * 20 + 6);
            foreach (string key in _working.Order)
            {
                int idx = _order.Items.Add(new SecItem(key));
                _order.SetItemChecked(idx, _working.GetShow(key));
            }
            _order.ItemCheck += delegate { BeginInvoke(new MethodInvoker(OnChanged)); };
            _panel.Controls.Add(_order);

            Button up = SmallButton("▲", _order.Right + 10, _y);
            up.Click += delegate { MoveItem(-1); };
            _panel.Controls.Add(up);
            Button down = SmallButton("▼", _order.Right + 10, _y + 36);
            down.Click += delegate { MoveItem(1); };
            _panel.Controls.Add(down);
            _y += _order.Height + 8;
        }

        private void BuildGraphs()
        {
            Hint("Per graph: accent color, max (0 = auto), and red alert threshold (0 = off).");
            Label(120, _y, 70, "color", Dim);
            Label(250, _y, 50, "max", Dim);
            Label(330, _y, 50, "alert", Dim);
            _y += 22;

            string[] names = { "CPU", "GPU", "MEM", "DISK", "NET" };
            Color[] colors = { _working.CpuColor, _working.GpuColor, _working.MemColor, _working.DiskColor, Color.Empty };
            double[] maxes = { _working.CpuMax, _working.GpuMax, _working.MemMax, _working.DiskMax, _working.NetMax };
            double[] alerts = { _working.CpuAlert, _working.GpuAlert, _working.MemAlert, _working.DiskAlert, _working.NetAlert };

            for (int i = 0; i < 5; i++)
            {
                Label(LabelX, _y + 4, 60, names[i], Ink);
                if (i < 4)
                {
                    Button c = new Button();
                    c.SetBounds(120, _y, 110, 26);
                    c.BackColor = colors[i];
                    c.FlatStyle = FlatStyle.Flat;
                    c.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 98);
                    c.Click += delegate { if (PickColor(c)) OnChanged(); };
                    _panel.Controls.Add(c);
                    _gColor[i] = c;
                }
                else
                {
                    Label(120, _y + 4, 130, "(see Network)", Dim);
                }
                _gMax[i] = GraphNum(250, maxes[i]);
                _gAlert[i] = GraphNum(330, alerts[i]);
                _y += 32;
            }
        }

        private void BuildClockDate()
        {
            _seconds   = AddCheck("Show seconds", _working.ShowSeconds, "Show HH:mm:ss instead of HH:mm.");
            _clockSize = AddNum("Clock size (pt)", 8, 160, (int)_working.ClockSize, null);
            _clockColor = AddColor("Clock color", _working.ClockColor);
            _dateSize  = AddNum("Date size (pt)", 8, 96, (int)_working.DateSize, null);
            _dateColor = AddColor("Date color", _working.DateColor);
            _dateBold  = AddCheck("Date bold", _working.DateBold, null);
            _daySize   = AddNum("Weekday size (pt)", 8, 96, (int)_working.DaySize, null);
            _dayColor  = AddColor("Weekday color", _working.DayColor);
            _dayBold   = AddCheck("Weekday bold", _working.DayBold, null);
        }

        private void BuildDrivesLists()
        {
            _driveLblSize = AddNum("Drive label size (pt)", 7, 28, (int)_working.DriveLabelSize, null);
            _driveLblBold = AddCheck("Drive label bold", _working.DriveLabelBold, null);
            _listSize = AddNum("Top CPU/RAM size (pt)", 7, 28, (int)_working.ListSize, "Text size of the top-process lists.");
            _ipSize   = AddNum("IP text size (pt)", 7, 28, (int)_working.IpSize, "Text size of the LAN/WAN lines.");
        }

        private void BuildNetwork()
        {
            _netBytes = AddCheck("Network in bytes", _working.NetUnitBytes, "Checked = MB/s & kB/s (bytes). Unchecked = Mbps & Kbps (bits).");
            _netDownColor = AddColor("Download color", _working.NetDownColor);
            _netUpColor   = AddColor("Upload color", _working.NetUpColor);
            _netTotalsSize = AddNum("Net totals size (pt)", 7, 28, (int)_working.NetTotalsSize, null);
            _ipLanSec = AddNum("LAN IP refresh (s)", 2, 3600, _working.IpLanRefreshSec, "How often the local IP is re-read.");
            _ipWanSec = AddNum("WAN IP refresh (s)", 30, 86400, _working.IpWanRefreshSec, "How often the public IP is looked up.");
        }

        private void BuildBehavior()
        {
            Label(LabelX, _y + 4, LabelW, "Opacity", Ink).TextAlign = ContentAlignment.MiddleRight;
            _opacity = new TrackBar();
            _opacity.Minimum = 30; _opacity.Maximum = 100; _opacity.TickFrequency = 10;
            _opacity.Value = Clamp((int)Math.Round(_working.Opacity * 100), 30, 100);
            _opacity.SetBounds(CtrlX - 4, _y, 150, 40);
            _opacity.Scroll += delegate { _opacityVal.Text = _opacity.Value + "%"; OnChanged(); };
            _panel.Controls.Add(_opacity);
            _opacityVal = new Label();
            _opacityVal.Text = _opacity.Value + "%";
            _opacityVal.ForeColor = Ink;
            _opacityVal.SetBounds(CtrlX + 150, _y + 8, 44, 20);
            _panel.Controls.Add(_opacityVal);
            _y += 46;

            _top = AddCheck("Always on top", _working.AlwaysOnTop, "Float above other windows. Uncheck = normal window.");
            _lock = AddCheck("Lock position", _working.Locked, "Disable dragging.");
            _extIp = AddCheck("Show external IP", _working.ExternalIpEnabled, "Look up your public IP (outbound HTTPS to api.ipify.org).");
            _startup = AddCheck("Start with Windows", Startup.IsEnabled(), "Add a shortcut to the Startup folder.");
            _debugLog = AddCheck("Write debug log", _working.DebugLog, "Log to %APPDATA%\\LoadView\\loadview.log for troubleshooting.");
        }

        private void BuildDefaults()
        {
            Label(LabelX, _y, 380, "Save the current configuration as your personal defaults,", Dim);
            _y += 20;
            Label(LabelX, _y, 380, "or reset everything back to them.", Dim);
            _y += 32;

            Button save = new Button();
            save.Text = "Save current as defaults";
            save.SetBounds(LabelX, _y, 200, 30);
            StyleButton(save);
            save.Click += delegate { SaveAsDefaults(); };
            _panel.Controls.Add(save);

            Button reset = new Button();
            reset.Text = "Reset to defaults";
            reset.SetBounds(LabelX + 210, _y, 160, 30);
            StyleButton(reset);
            reset.Click += delegate { ResetToDefaults(); };
            _panel.Controls.Add(reset);
        }

        // ---------- buttons / commit ----------

        private void BuildButtons(Panel bottom)
        {
            const int bw = 90, bh = 32, bgap = 10;
            int right = 660 - 16;

            Button ok = new Button();
            ok.Text = "OK";
            ok.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            ok.SetBounds(right - bw * 2 - bgap, 10, bw, bh);
            StyleButton(ok);
            ok.Click += delegate { CommitToWorking(); Startup.SetEnabled(_startup.Checked); DialogResult = DialogResult.OK; };
            bottom.Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            cancel.SetBounds(right - bw, 10, bw, bh);
            StyleButton(cancel);
            bottom.Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        // Push the live edit to the overlay (no disk write).
        private void OnChanged()
        {
            if (_suspend) return;
            CommitToWorking();
            if (_preview != null) _preview(_working.Clone());
        }

        private void SaveAsDefaults()
        {
            CommitToWorking();
            _working.SaveAsDefaults();
            MessageBox.Show(this, "Saved the current settings as the defaults.", "LoadView",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ResetToDefaults()
        {
            BeginInvoke(new MethodInvoker(delegate
            {
                _working = Settings.LoadDefaults();
                int sel = _nav.SelectedIndex;
                _suspend = true;
                _host.Controls.Clear();
                _pages.Clear();
                _nav.Items.Clear();
                BuildAllPages();
                _suspend = false;
                _nav.SelectedIndex = (sel >= 0 && sel < _nav.Items.Count) ? sel : 0;
                if (_preview != null) _preview(_working.Clone());
            }));
        }

        private void MoveItem(int delta)
        {
            int i = _order.SelectedIndex;
            if (i < 0) return;
            int j = i + delta;
            if (j < 0 || j >= _order.Items.Count) return;
            object item = _order.Items[i];
            bool chk = _order.GetItemChecked(i);
            _order.Items.RemoveAt(i);
            _order.Items.Insert(j, item);
            _order.SetItemChecked(j, chk);
            _order.SelectedIndex = j;
            OnChanged();
        }

        private void CommitToWorking()
        {
            _working.Width = (int)_width.Value;
            _working.GraphHeight = (int)_graphH.Value;
            _working.DriveRowHeight = (int)_driveH.Value;
            _working.RefreshMs = (int)_refreshMs.Value;

            List<string> order = new List<string>();
            for (int i = 0; i < _order.Items.Count; i++)
            {
                string key = ((SecItem)_order.Items[i]).Key;
                order.Add(key);
                _working.SetShow(key, _order.GetItemChecked(i));
            }
            _working.Order = order;

            _working.CpuColor = _gColor[0].BackColor; _working.CpuMax = (double)_gMax[0].Value; _working.CpuAlert = (double)_gAlert[0].Value;
            _working.GpuColor = _gColor[1].BackColor; _working.GpuMax = (double)_gMax[1].Value; _working.GpuAlert = (double)_gAlert[1].Value;
            _working.MemColor = _gColor[2].BackColor; _working.MemMax = (double)_gMax[2].Value; _working.MemAlert = (double)_gAlert[2].Value;
            _working.DiskColor = _gColor[3].BackColor; _working.DiskMax = (double)_gMax[3].Value; _working.DiskAlert = (double)_gAlert[3].Value;
            _working.NetMax = (double)_gMax[4].Value; _working.NetAlert = (double)_gAlert[4].Value;

            _working.NetUnitBytes = _netBytes.Checked;
            _working.NetDownColor = _netDownColor.BackColor;
            _working.NetUpColor = _netUpColor.BackColor;
            _working.NetTotalsSize = (float)_netTotalsSize.Value;
            _working.IpLanRefreshSec = (int)_ipLanSec.Value;
            _working.IpWanRefreshSec = (int)_ipWanSec.Value;

            _working.ShowSeconds = _seconds.Checked;
            _working.ClockSize = (float)_clockSize.Value;
            _working.ClockColor = _clockColor.BackColor;
            _working.DateSize = (float)_dateSize.Value;
            _working.DateColor = _dateColor.BackColor;
            _working.DateBold = _dateBold.Checked;
            _working.DaySize = (float)_daySize.Value;
            _working.DayColor = _dayColor.BackColor;
            _working.DayBold = _dayBold.Checked;

            _working.DriveLabelSize = (float)_driveLblSize.Value;
            _working.DriveLabelBold = _driveLblBold.Checked;
            _working.ListSize = (float)_listSize.Value;
            _working.IpSize = (float)_ipSize.Value;

            _working.Opacity = _opacity.Value / 100.0;
            _working.AlwaysOnTop = _top.Checked;
            _working.Locked = _lock.Checked;
            _working.ExternalIpEnabled = _extIp.Checked;
            _working.DebugLog = _debugLog.Checked;
        }

        // ---------- row builders (right-aligned label + control) ----------

        private Label Hint(string text)
        {
            Label l = Label(LabelX, _y, 470, text, Dim);
            _y += 26;
            return l;
        }

        private Label Label(int x, int y, int w, string text, Color color)
        {
            Label l = new Label();
            l.Text = text;
            l.ForeColor = color;
            l.SetBounds(x, y, w, 20);
            _panel.Controls.Add(l);
            return l;
        }

        private Label RowLabel(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.ForeColor = Ink;
            l.TextAlign = ContentAlignment.MiddleRight;
            l.SetBounds(LabelX, _y, LabelW, 24);
            _panel.Controls.Add(l);
            return l;
        }

        private NumericUpDown AddNum(string label, int min, int max, int val, string tip)
        {
            RowLabel(label);
            NumericUpDown n = new NumericUpDown();
            n.Minimum = min; n.Maximum = max; n.DecimalPlaces = 0;
            n.Value = Clamp(val, min, max);
            n.BackColor = FieldBg; n.ForeColor = Ink; n.BorderStyle = BorderStyle.FixedSingle;
            n.SetBounds(CtrlX, _y, 90, 24);
            n.ValueChanged += delegate { OnChanged(); };
            if (tip != null) _tips.SetToolTip(n, tip);
            _panel.Controls.Add(n);
            _y += 30;
            return n;
        }

        private NumericUpDown GraphNum(int x, double val)
        {
            NumericUpDown n = new NumericUpDown();
            n.Minimum = 0; n.Maximum = 100000; n.DecimalPlaces = 0;
            n.Value = (decimal)Clamp((int)val, 0, 100000);
            n.BackColor = FieldBg; n.ForeColor = Ink; n.BorderStyle = BorderStyle.FixedSingle;
            n.SetBounds(x, _y, 72, 24);
            n.ValueChanged += delegate { OnChanged(); };
            _panel.Controls.Add(n);
            return n;
        }

        private Button AddColor(string label, Color c)
        {
            RowLabel(label);
            Button b = new Button();
            b.SetBounds(CtrlX, _y, 90, 24);
            b.BackColor = c;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 98);
            Label hex = new Label();
            hex.SetBounds(CtrlX + 100, _y + 3, 70, 20);
            hex.ForeColor = Dim;
            hex.Text = Hex(c);
            b.Click += delegate { if (PickColor(b)) { hex.Text = Hex(b.BackColor); OnChanged(); } };
            _panel.Controls.Add(b);
            _panel.Controls.Add(hex);
            _y += 30;
            return b;
        }

        private CheckBox AddCheck(string label, bool val, string tip)
        {
            RowLabel(label);
            CheckBox c = new CheckBox();
            c.Checked = val;
            c.ForeColor = Ink;
            c.FlatStyle = FlatStyle.Flat;
            c.SetBounds(CtrlX, _y + 1, 22, 22);
            c.CheckedChanged += delegate { OnChanged(); };
            if (tip != null) _tips.SetToolTip(c, tip);
            _panel.Controls.Add(c);
            _y += 30;
            return c;
        }

        private bool PickColor(Button b)
        {
            using (ColorDialog cd = new ColorDialog())
            {
                cd.Color = b.BackColor;
                cd.FullOpen = true;
                if (cd.ShowDialog(this) == DialogResult.OK) { b.BackColor = cd.Color; return true; }
            }
            return false;
        }

        private Button SmallButton(string text, int x, int y)
        {
            Button b = new Button();
            b.Text = text;
            b.SetBounds(x, y, 32, 32);
            StyleButton(b);
            return b;
        }

        private static void StyleButton(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Color.FromArgb(56, 56, 64);
            b.ForeColor = Color.FromArgb(232, 232, 237);
            b.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 98);
        }

        private static string Hex(Color c)
        {
            return string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
        }

        private static int Clamp(int v, int lo, int hi) { if (v < lo) return lo; if (v > hi) return hi; return v; }

        private sealed class SecItem
        {
            public readonly string Key;
            public SecItem(string key) { Key = key; }
            public override string ToString() { return Settings.DisplayName(Key); }
        }
    }
}
