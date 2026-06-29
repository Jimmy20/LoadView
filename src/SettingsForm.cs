using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace LoadView
{
    // Edits a working copy of Settings. Apply/OK invoke the supplied callback live.
    // Centered on screen; the option area scrolls. Roomy layout for easy editing.
    internal sealed class SettingsForm : Form
    {
        private static readonly Color Bg = Color.FromArgb(32, 32, 36);
        private static readonly Color Ink = Color.FromArgb(232, 232, 237);
        private static readonly Color Accent = Color.FromArgb(0x6F, 0xA8, 0xFF);
        private static readonly Color FieldBg = Color.FromArgb(46, 46, 52);

        private readonly Settings _working;
        private readonly Action<Settings> _apply;

        private Panel _content;
        private int _y = 14;
        private const int LabelX = 20;
        private const int CtrlX = 250;
        private const int CtrlW = 190;
        private const int RowH = 28;
        private const int ContentW = 470;

        // controls
        private NumericUpDown _width, _graphH, _driveH, _refreshMs, _clockSize, _dateSize, _daySize, _driveLblSize;
        private CheckBox _seconds, _dateBold, _dayBold, _driveLblBold, _netBytes, _extIp, _top, _lock, _startup, _debugLog;
        private Button _clockColor, _dateColor, _dayColor;
        private CheckedListBox _order;
        private TrackBar _opacity;
        private Label _opacityVal;

        private readonly Button[] _gColor = new Button[5];
        private readonly NumericUpDown[] _gMax = new NumericUpDown[5];
        private readonly NumericUpDown[] _gAlert = new NumericUpDown[5];

        public SettingsForm(Settings working, Action<Settings> apply)
        {
            _working = working;
            _apply = apply;

            Text = "LoadView Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen; // always center of screen
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Bg;
            ForeColor = Ink;
            Font = new Font("Segoe UI", 9.5f);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(ContentW, 660);

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 48;
            bottom.BackColor = Bg;
            Controls.Add(bottom);

            _content = new Panel();
            _content.Dock = DockStyle.Fill;
            _content.AutoScroll = true;
            _content.BackColor = Bg;
            Controls.Add(_content);
            _content.BringToFront();

            BuildContent();
            BuildButtons(bottom);
        }

        private void BuildContent()
        {
            Section("Layout");
            _width = AddNum("Window width (px)", 180, 800, _working.Width);
            _graphH = AddNum("Graph height (px)", 24, 240, _working.GraphHeight);
            _driveH = AddNum("Drive bar height (px)", 16, 120, _working.DriveRowHeight);
            _refreshMs = AddNum("Refresh interval (ms)", 200, 10000, _working.RefreshMs);
            _refreshMs.Increment = 100;
            _netBytes = AddCheckFull("Network in bytes (MB/s) — uncheck for bits (Mbps)", _working.NetUnitBytes);

            Section("Sections  (check = visible · ▲▼ = reorder)");
            BuildReorder();

            Section("Graphs  (color · max 0=auto · alert%, 0=off)");
            BuildGraphRows();

            Section("Clock / date");
            _seconds = AddCheckFull("Show seconds", _working.ShowSeconds);
            _clockSize = AddNum("Clock size (pt)", 8, 72, (int)_working.ClockSize);
            _clockColor = AddColor("Clock color", _working.ClockColor);
            _dateSize = AddNum("Date size (pt)", 8, 48, (int)_working.DateSize);
            _dateColor = AddColor("Date color", _working.DateColor);
            _dateBold = AddCheckFull("Date bold", _working.DateBold);
            _daySize = AddNum("Weekday size (pt)", 8, 48, (int)_working.DaySize);
            _dayColor = AddColor("Weekday color", _working.DayColor);
            _dayBold = AddCheckFull("Weekday bold", _working.DayBold);

            Section("Drives");
            _driveLblSize = AddNum("Drive label size (pt)", 7, 24, (int)_working.DriveLabelSize);
            _driveLblBold = AddCheckFull("Drive label bold", _working.DriveLabelBold);

            Section("Behavior");
            AddLabel("Opacity", LabelX, _y + 3, 150);
            _opacity = new TrackBar();
            _opacity.Minimum = 30; _opacity.Maximum = 100; _opacity.TickFrequency = 10;
            _opacity.Value = Clamp((int)Math.Round(_working.Opacity * 100), 30, 100);
            _opacity.SetBounds(CtrlX - 6, _y, CtrlW - 30, 40);
            _opacity.Scroll += delegate { _opacityVal.Text = _opacity.Value + "%"; };
            _content.Controls.Add(_opacity);
            _opacityVal = new Label();
            _opacityVal.Text = _opacity.Value + "%";
            _opacityVal.ForeColor = Ink;
            _opacityVal.SetBounds(CtrlX + CtrlW - 32, _y + 6, 40, 20);
            _content.Controls.Add(_opacityVal);
            _y += 46;
            _extIp = AddCheckFull("Show external (public) IP", _working.ExternalIpEnabled);
            _startup = AddCheckFull("Start with Windows", Startup.IsEnabled());
            _debugLog = AddCheckFull("Write debug log (%APPDATA%\\LoadView\\loadview.log)", _working.DebugLog);
            _top = AddCheckFull("Always on top (uncheck = normal window)", _working.AlwaysOnTop);
            _lock = AddCheckFull("Lock position (no dragging)", _working.Locked);

            _y += 10;
        }

        private void BuildReorder()
        {
            _order = new CheckedListBox();
            _order.BackColor = FieldBg;
            _order.ForeColor = Ink;
            _order.BorderStyle = BorderStyle.FixedSingle;
            _order.IntegralHeight = false;
            _order.CheckOnClick = true;
            _order.SetBounds(LabelX, _y, ContentW - 2 * LabelX - 46, Settings.AllSections.Length * 19 + 6);
            foreach (string key in _working.Order)
            {
                int idx = _order.Items.Add(new SecItem(key));
                _order.SetItemChecked(idx, _working.GetShow(key));
            }
            _content.Controls.Add(_order);

            Button up = SmallButton("▲", _order.Right + 8, _y);
            up.Click += delegate { MoveItem(-1); };
            _content.Controls.Add(up);
            Button down = SmallButton("▼", _order.Right + 8, _y + 34);
            down.Click += delegate { MoveItem(1); };
            _content.Controls.Add(down);

            _y += _order.Height + 10;
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
        }

        private void BuildGraphRows()
        {
            AddLabel("color", 100, _y, 60);
            AddLabel("max", 230, _y, 50);
            AddLabel("alert", 320, _y, 50);
            _y += 20;

            string[] names = new string[] { "CPU", "GPU", "MEM", "DISK", "NET" };
            Color[] colors = new Color[] { _working.CpuColor, _working.GpuColor, _working.MemColor, _working.DiskColor, _working.NetColor };
            double[] maxes = new double[] { _working.CpuMax, _working.GpuMax, _working.MemMax, _working.DiskMax, _working.NetMax };
            double[] alerts = new double[] { _working.CpuAlert, _working.GpuAlert, _working.MemAlert, _working.DiskAlert, _working.NetAlert };

            for (int i = 0; i < 5; i++)
            {
                AddLabel(names[i], LabelX, _y + 4, 60);

                Button c = new Button();
                c.SetBounds(100, _y, 110, RowH);
                c.BackColor = colors[i];
                c.FlatStyle = FlatStyle.Flat;
                c.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 98);
                c.Click += delegate { PickColor(c); };
                _content.Controls.Add(c);
                _gColor[i] = c;

                _gMax[i] = MakeNum(230, 0, 100000, maxes[i]);
                _gAlert[i] = MakeNum(320, 0, 100000, alerts[i]);
                _y += RowH + 6;
            }
        }

        private void BuildButtons(Panel bottom)
        {
            const int bw = 82, bh = 30, bgap = 8;
            int rightEdge = ContentW - 16;

            Button ok = new Button();
            ok.Text = "OK";
            ok.DialogResult = DialogResult.OK;
            ok.SetBounds(rightEdge - bw * 3 - bgap * 2, 9, bw, bh);
            StyleButton(ok);
            ok.Click += delegate { ApplyNow(); };
            bottom.Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(rightEdge - bw * 2 - bgap, 9, bw, bh);
            StyleButton(cancel);
            bottom.Controls.Add(cancel);

            Button applyBtn = new Button();
            applyBtn.Text = "Apply";
            applyBtn.SetBounds(rightEdge - bw, 9, bw, bh);
            StyleButton(applyBtn);
            applyBtn.Click += delegate { ApplyNow(); };
            bottom.Controls.Add(applyBtn);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        private void ApplyNow()
        {
            CommitToWorking();
            if (_apply != null) _apply(_working.Clone());
        }

        private void CommitToWorking()
        {
            _working.Width = (int)_width.Value;
            _working.GraphHeight = (int)_graphH.Value;
            _working.DriveRowHeight = (int)_driveH.Value;
            _working.RefreshMs = (int)_refreshMs.Value;
            _working.NetUnitBytes = _netBytes.Checked;

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
            _working.NetColor = _gColor[4].BackColor; _working.NetMax = (double)_gMax[4].Value; _working.NetAlert = (double)_gAlert[4].Value;

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

            _working.Opacity = _opacity.Value / 100.0;
            _working.ExternalIpEnabled = _extIp.Checked;
            _working.DebugLog = _debugLog.Checked;
            _working.AlwaysOnTop = _top.Checked;
            _working.Locked = _lock.Checked;

            Startup.SetEnabled(_startup.Checked); // system Run key, not part of settings.ini
        }

        // ---- builders ----

        private void Section(string title)
        {
            Label l = new Label();
            l.Text = title;
            l.Font = new Font(Font, FontStyle.Bold);
            l.ForeColor = Accent;
            l.SetBounds(LabelX, _y, ContentW - 2 * LabelX, 20);
            _content.Controls.Add(l);
            _y += 24;
        }

        private Label AddLabel(string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.ForeColor = Ink;
            l.SetBounds(x, y, w, 20);
            _content.Controls.Add(l);
            return l;
        }

        private NumericUpDown AddNum(string label, int min, int max, int val)
        {
            AddLabel(label, LabelX, _y + 4, CtrlX - LabelX - 4);
            NumericUpDown n = MakeNum(CtrlX, min, max, val);
            n.Width = CtrlW;
            _y += RowH + 5;
            return n;
        }

        private NumericUpDown MakeNum(int x, double min, double max, double val)
        {
            NumericUpDown n = new NumericUpDown();
            n.Minimum = (decimal)min;
            n.Maximum = (decimal)max;
            n.DecimalPlaces = 0;
            n.Value = (decimal)Clamp(val, min, max);
            n.BackColor = FieldBg;
            n.ForeColor = Ink;
            n.BorderStyle = BorderStyle.FixedSingle;
            n.SetBounds(x, _y, 76, RowH);
            _content.Controls.Add(n);
            return n;
        }

        private Button AddColor(string label, Color c)
        {
            AddLabel(label, LabelX, _y + 4, CtrlX - LabelX - 4);
            Button b = new Button();
            b.SetBounds(CtrlX, _y, CtrlW, RowH);
            b.BackColor = c;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 98);
            b.Click += delegate { PickColor(b); };
            _content.Controls.Add(b);
            _y += RowH + 5;
            return b;
        }

        private void PickColor(Button b)
        {
            using (ColorDialog cd = new ColorDialog())
            {
                cd.Color = b.BackColor;
                cd.FullOpen = true;
                if (cd.ShowDialog(this) == DialogResult.OK)
                    b.BackColor = cd.Color;
            }
        }

        private CheckBox AddCheckFull(string label, bool val)
        {
            CheckBox c = new CheckBox();
            c.Text = label;
            c.Checked = val;
            c.ForeColor = Ink;
            c.FlatStyle = FlatStyle.Flat;
            c.SetBounds(LabelX, _y, ContentW - 2 * LabelX, RowH);
            _content.Controls.Add(c);
            _y += RowH;
            return c;
        }

        private Button SmallButton(string text, int x, int y)
        {
            Button b = new Button();
            b.Text = text;
            b.SetBounds(x, y, 30, 30);
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

        private static int Clamp(int v, int lo, int hi) { if (v < lo) return lo; if (v > hi) return hi; return v; }
        private static double Clamp(double v, double lo, double hi) { if (v < lo) return lo; if (v > hi) return hi; return v; }

        private sealed class SecItem
        {
            public readonly string Key;
            public SecItem(string key) { Key = key; }
            public override string ToString() { return Settings.DisplayName(Key); }
        }
    }
}
