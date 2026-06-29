using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace LoadView
{
    // Edits a working copy of Settings. On OK, Result holds the edited settings.
    internal sealed class SettingsForm : Form
    {
        private static readonly Color Bg = Color.FromArgb(32, 32, 36);
        private static readonly Color Ink = Color.FromArgb(232, 232, 237);
        private static readonly Color Accent = Color.FromArgb(0x6F, 0xA8, 0xFF);
        private static readonly Color FieldBg = Color.FromArgb(46, 46, 52);

        private readonly Settings _working;

        private NumericUpDown _width, _graphH, _driveH, _clockSize, _dateSize, _daySize;
        private CheckBox _seconds, _top, _lock;
        private Button _clockColor, _dateColor, _dayColor;
        private CheckBox _shClock, _shCpu, _shGpu, _shMem, _shDisk, _shNet, _shDrives, _shFooter;
        private TrackBar _opacity;
        private Label _opacityVal;

        private int _y = 12;
        private const int LabelX = 16;
        private const int CtrlX = 190;
        private const int CtrlW = 150;
        private const int RowH = 26;

        private readonly Action<Settings> _apply;

        public SettingsForm(Settings working, Action<Settings> apply)
        {
            _working = working;
            _apply = apply;

            Text = "LoadView Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Bg;
            ForeColor = Ink;
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Font;

            Section("Layout");
            _width = AddNum("Window width (px)", 180, 800, _working.Width);
            _graphH = AddNum("Graph height (px)", 24, 240, _working.GraphHeight);
            _driveH = AddNum("Drive bar height (px)", 16, 120, _working.DriveRowHeight);

            Section("Clock / date");
            _seconds = AddCheckFull("Show seconds", _working.ShowSeconds);
            _clockSize = AddNum("Clock size (pt)", 8, 72, (int)_working.ClockSize);
            _dateSize = AddNum("Date size (pt)", 8, 48, (int)_working.DateSize);
            _daySize = AddNum("Weekday size (pt)", 8, 48, (int)_working.DaySize);
            _clockColor = AddColor("Clock color", _working.ClockColor);
            _dateColor = AddColor("Date color", _working.DateColor);
            _dayColor = AddColor("Weekday color", _working.DayColor);

            Section("Sections");
            int rowTop = _y;
            _shClock = AddCheckAt("Clock", _working.ShowClock, LabelX, rowTop);
            _shCpu = AddCheckAt("CPU", _working.ShowCpu, LabelX, rowTop + RowH);
            _shGpu = AddCheckAt("GPU", _working.ShowGpu, LabelX, rowTop + RowH * 2);
            _shMem = AddCheckAt("MEM", _working.ShowMem, LabelX, rowTop + RowH * 3);
            _shDisk = AddCheckAt("DISK", _working.ShowDisk, CtrlX, rowTop);
            _shNet = AddCheckAt("NET", _working.ShowNet, CtrlX, rowTop + RowH);
            _shDrives = AddCheckAt("Drives", _working.ShowDrives, CtrlX, rowTop + RowH * 2);
            _shFooter = AddCheckAt("Date / weekday", _working.ShowFooter, CtrlX, rowTop + RowH * 3);
            _y = rowTop + RowH * 4 + 4;

            Section("Behavior");
            AddLabel("Opacity", LabelX, _y + 3, 150);
            _opacity = new TrackBar();
            _opacity.Minimum = 30; _opacity.Maximum = 100; _opacity.TickFrequency = 10;
            _opacity.Value = Clamp((int)Math.Round(_working.Opacity * 100), 30, 100);
            _opacity.SetBounds(CtrlX - 6, _y, CtrlW - 30, 40);
            _opacity.Scroll += delegate { _opacityVal.Text = _opacity.Value + "%"; };
            Controls.Add(_opacity);
            _opacityVal = new Label();
            _opacityVal.Text = _opacity.Value + "%";
            _opacityVal.SetBounds(CtrlX + CtrlW - 32, _y + 6, 40, 20);
            _opacityVal.ForeColor = Ink;
            Controls.Add(_opacityVal);
            _y += 44;
            _top = AddCheckFull("Always on top (uncheck = normal window)", _working.AlwaysOnTop);
            _lock = AddCheckFull("Lock position (no dragging)", _working.Locked);

            // buttons: OK (apply + close), Cancel, Apply (apply + stay open)
            _y += 8;
            const int bw = 72, bh = 28, bgap = 6;
            int rightEdge = CtrlX + CtrlW;

            Button ok = new Button();
            ok.Text = "OK";
            ok.DialogResult = DialogResult.OK;
            ok.SetBounds(rightEdge - bw * 3 - bgap * 2, _y, bw, bh);
            StyleButton(ok);
            ok.Click += delegate { ApplyNow(); };
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(rightEdge - bw * 2 - bgap, _y, bw, bh);
            StyleButton(cancel);
            Controls.Add(cancel);

            Button applyBtn = new Button();
            applyBtn.Text = "Apply";
            applyBtn.SetBounds(rightEdge - bw, _y, bw, bh);
            StyleButton(applyBtn);
            applyBtn.Click += delegate { ApplyNow(); };
            Controls.Add(applyBtn);

            AcceptButton = ok;
            CancelButton = cancel;

            ClientSize = new Size(CtrlX + CtrlW + 16, _y + bh + 14);
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

            _working.ShowSeconds = _seconds.Checked;
            _working.ClockSize = (float)_clockSize.Value;
            _working.DateSize = (float)_dateSize.Value;
            _working.DaySize = (float)_daySize.Value;
            _working.ClockColor = _clockColor.BackColor;
            _working.DateColor = _dateColor.BackColor;
            _working.DayColor = _dayColor.BackColor;

            _working.ShowClock = _shClock.Checked;
            _working.ShowCpu = _shCpu.Checked;
            _working.ShowGpu = _shGpu.Checked;
            _working.ShowMem = _shMem.Checked;
            _working.ShowDisk = _shDisk.Checked;
            _working.ShowNet = _shNet.Checked;
            _working.ShowDrives = _shDrives.Checked;
            _working.ShowFooter = _shFooter.Checked;

            _working.Opacity = _opacity.Value / 100.0;
            _working.AlwaysOnTop = _top.Checked;
            _working.Locked = _lock.Checked;
        }

        // ---- builders ----

        private void Section(string title)
        {
            Label l = new Label();
            l.Text = title;
            l.Font = new Font(Font, FontStyle.Bold);
            l.ForeColor = Accent;
            l.SetBounds(LabelX, _y, CtrlX + CtrlW - LabelX, 18);
            Controls.Add(l);
            _y += 22;
        }

        private Label AddLabel(string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.ForeColor = Ink;
            l.SetBounds(x, y, w, 20);
            Controls.Add(l);
            return l;
        }

        private NumericUpDown AddNum(string label, int min, int max, int val)
        {
            AddLabel(label, LabelX, _y + 3, CtrlX - LabelX - 4);
            NumericUpDown n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.Value = Clamp(val, min, max);
            n.DecimalPlaces = 0;
            n.BackColor = FieldBg;
            n.ForeColor = Ink;
            n.BorderStyle = BorderStyle.FixedSingle;
            n.SetBounds(CtrlX, _y, CtrlW, RowH);
            Controls.Add(n);
            _y += RowH + 4;
            return n;
        }

        private Button AddColor(string label, Color c)
        {
            AddLabel(label, LabelX, _y + 3, CtrlX - LabelX - 4);
            Button b = new Button();
            b.SetBounds(CtrlX, _y, CtrlW, RowH);
            b.BackColor = c;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 98);
            b.Click += delegate { PickColor(b); };
            Controls.Add(b);
            _y += RowH + 4;
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
            CheckBox c = AddCheckAt(label, val, LabelX, _y);
            c.Width = CtrlX + CtrlW - LabelX;
            _y += RowH;
            return c;
        }

        private CheckBox AddCheckAt(string label, bool val, int x, int y)
        {
            CheckBox c = new CheckBox();
            c.Text = label;
            c.Checked = val;
            c.ForeColor = Ink;
            c.FlatStyle = FlatStyle.Flat;
            c.SetBounds(x, y, 165, RowH);
            Controls.Add(c);
            return c;
        }

        private static void StyleButton(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Color.FromArgb(56, 56, 64);
            b.ForeColor = Color.FromArgb(232, 232, 237);
            b.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 98);
        }

        private static int Clamp(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
    }
}
