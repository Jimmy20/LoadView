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
            _driveLblSize, _listSize, _ipSize, _netTotalsSize, _ipLanSec, _ipWanSec, _tempHot;
        private CheckBox _seconds, _dateBold, _dayBold, _driveLblBold, _extIp, _top, _lock, _startup, _debugLog,
            _showCpuTemp, _showGpuTemp, _accurateDriver, _showWanCountry, _showWanFlag, _tempHotOn;
        private ComboBox _netUnit, _tempUnit;
        private Button _clockColor, _dateColor, _dayColor, _netDownColor, _netUpColor;
        private CheckedListBox _order;
        private TrackBar _opacity;
        private Label _opacityVal, _tempHotEquiv;
        private readonly Button[] _gColor = new Button[5];
        private readonly NumericUpDown[] _gMax = new NumericUpDown[5];
        private readonly NumericUpDown[] _gAlert = new NumericUpDown[5];
        private readonly CheckBox[] _gAuto = new CheckBox[5];
        private readonly CheckBox[] _gAlertOn = new CheckBox[5];

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

        // One page per subject. The old split had graph height under "Layout", the NET graph's colours
        // on a different page from its scale, and the IP section's options spread over three pages —
        // so each page here owns everything about one thing, and nothing else.
        private void BuildAllPages()
        {
            AddPage("Window", BuildWindow);
            AddPage("Sections", BuildSections);
            AddPage("Graphs", BuildGraphs);
            AddPage("Clock & date", BuildClockDate);
            AddPage("Drives & processes", BuildDrivesProcesses);
            AddPage("Network & IP", BuildNetwork);
            AddPage("Temperatures", BuildTemperatures);
            AddPage("Advanced", BuildAdvanced);
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

        private void BuildWindow()
        {
            _width = AddNum("Window width (px)", Settings.MinWidth, Settings.MaxWidth, _working.Width,
                "Overall width of the overlay panel.");

            RowLabel("Opacity");
            _opacity = new TrackBar();
            _opacity.Minimum = 30; _opacity.Maximum = 100; _opacity.TickFrequency = 10;
            _opacity.Value = Clamp((int)Math.Round(_working.Opacity * 100), 30, 100);
            _opacity.SetBounds(CtrlX - 4, _y, 150, 40);
            _opacity.Scroll += delegate { _opacityVal.Text = _opacity.Value + "%"; OnChanged(); };
            _tips.SetToolTip(_opacity, "How see-through the overlay is.");
            _panel.Controls.Add(_opacity);
            _opacityVal = new Label();
            _opacityVal.Text = _opacity.Value + "%";
            _opacityVal.ForeColor = Ink;
            _opacityVal.SetBounds(CtrlX + 150, _y + 8, 44, 20);
            _panel.Controls.Add(_opacityVal);
            _y += 46;

            _top = AddCheck("Always on top", _working.AlwaysOnTop,
                "Float above other windows. Unchecked = an ordinary window other apps can cover.");
            _lock = AddCheck("Lock position", _working.Locked,
                "Stop the overlay being dragged. \"Reset position\" in the menu still works.");
            _startup = AddCheck("Start with Windows", Startup.IsEnabled(),
                "Add a shortcut to your Startup folder.");
        }

        private void BuildSections()
        {
            Hint("Check = visible.  Select an item and use ▲ ▼ to reorder.");
            Hint("Each section's own options live on the matching page.");
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
            _tips.SetToolTip(up, "Move the selected section up");
            _panel.Controls.Add(up);
            Button down = SmallButton("▼", _order.Right + 10, _y + 36);
            down.Click += delegate { MoveItem(1); };
            _tips.SetToolTip(down, "Move the selected section down");
            _panel.Controls.Add(down);
            _y += _order.Height + 8;
        }

        private void BuildGraphs()
        {
            _graphH = AddNum("Graph height (px)", Settings.MinGraphH, Settings.MaxGraphH, _working.GraphHeight,
                "Height of every metric graph. (This used to sit under Layout, which is why it was hard to find.)");
            _y += 6;

            // "Auto" and "Alert" are checkboxes rather than the old magic zero: the number fields used
            // to mean "0 = auto" and "0 = off", which was only explained in a hint above the table.
            // Column x-positions are tight on purpose: the page is 660 - 150 (nav) wide and loses
            // another ~17 to the scrollbar, so the last field has to end before ~490.
            Label(100, _y, 60, "colour", Dim);
            Label(196, _y, 60, "scale", Dim);
            Label(344, _y, 60, "red alert", Dim);
            _y += 22;

            string[] names = { "CPU", "GPU", "MEM", "DISK", "NET" };
            double[] maxes = { _working.CpuMax, _working.GpuMax, _working.MemMax, _working.DiskMax, _working.NetMax };
            double[] alerts = { _working.CpuAlert, _working.GpuAlert, _working.MemAlert, _working.DiskAlert, _working.NetAlert };
            Color[] colors = { _working.CpuColor, _working.GpuColor, _working.MemColor, _working.DiskColor, Color.Empty };

            for (int i = 0; i < 5; i++)
            {
                Label(LabelX, _y + 5, 60, names[i], Ink);

                if (i < 4)
                {
                    _gColor[i] = GraphColor(100, colors[i]);
                }
                else
                {
                    // The NET graph draws two series, so its two colours belong here with its scale
                    // rather than on the Network page, which is where they used to hide.
                    _netDownColor = GraphColor(100, _working.NetDownColor);
                    _tips.SetToolTip(_netDownColor, "Download series colour.");
                    _netUpColor = GraphColor(142, _working.NetUpColor);
                    _tips.SetToolTip(_netUpColor, "Upload series colour.");
                }

                bool autoScale = maxes[i] <= 0;
                _gAuto[i] = GraphToggle(196, "Auto", autoScale,
                    "Scale the graph to the highest recent value instead of a fixed maximum.");
                _gMax[i] = GraphNum(266, autoScale ? 0 : maxes[i]);
                _gMax[i].Enabled = !autoScale;
                _tips.SetToolTip(_gMax[i], i == 4 ? "Fixed top of the scale, in the unit chosen on the Network page."
                                                  : "Fixed top of the scale (percent).");

                bool alertOn = alerts[i] > 0;
                _gAlertOn[i] = GraphToggle(344, "At", alertOn, "Turn the graph red from this value upwards.");
                _gAlert[i] = GraphNum(396, alerts[i]);
                _gAlert[i].Enabled = alertOn;
                _tips.SetToolTip(_gAlert[i], "The graph goes red at or above this value.");

                int idx = i;   // capture for the closures below
                _gAuto[i].CheckedChanged += delegate
                {
                    _gMax[idx].Enabled = !_gAuto[idx].Checked;
                    OnChanged();
                };
                _gAlertOn[i].CheckedChanged += delegate
                {
                    _gAlert[idx].Enabled = _gAlertOn[idx].Checked;
                    OnChanged();
                };
                _y += 34;
            }
        }

        private Button GraphColor(int x, Color c)
        {
            Button b = new Button();
            b.SetBounds(x, _y, 38, 26);
            b.BackColor = c;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 98);
            b.Click += delegate { if (PickColor(b)) OnChanged(); };
            _panel.Controls.Add(b);
            return b;
        }

        private CheckBox GraphToggle(int x, string text, bool val, string tip)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.Checked = val;
            c.ForeColor = Ink;
            c.FlatStyle = FlatStyle.Flat;
            c.SetBounds(x, _y + 3, text.Length > 3 ? 62 : 44, 22);
            _tips.SetToolTip(c, tip);
            _panel.Controls.Add(c);
            return c;
        }

        private void BuildClockDate()
        {
            GroupHeader("Clock");
            _seconds = AddCheck("Show seconds", _working.ShowSeconds, "Show HH:mm:ss instead of HH:mm.");
            _clockSize = AddNum("Size (pt)", (int)Settings.MinBigPt, (int)Settings.MaxBigPt, (int)_working.ClockSize,
                "Text size of the big clock.");
            _clockColor = AddColor("Colour", _working.ClockColor);

            GroupHeader("Date");
            _dateSize = AddNum("Size (pt)", (int)Settings.MinBigPt, (int)Settings.MaxBigPt, (int)_working.DateSize,
                "Text size of the date line.");
            _dateColor = AddColor("Colour", _working.DateColor);
            _dateBold = AddCheck("Bold", _working.DateBold, "Draw the date in bold.");

            GroupHeader("Weekday");
            _daySize = AddNum("Size (pt)", (int)Settings.MinBigPt, (int)Settings.MaxBigPt, (int)_working.DaySize,
                "Text size of the weekday line.");
            _dayColor = AddColor("Colour", _working.DayColor);
            _dayBold = AddCheck("Bold", _working.DayBold, "Draw the weekday in bold.");
        }

        private void BuildDrivesProcesses()
        {
            GroupHeader("Drives");
            _driveH = AddNum("Row height (px)", Settings.MinDriveRow, Settings.MaxDriveRow, _working.DriveRowHeight,
                "Height of each drive row, including its usage bar.");
            _driveLblSize = AddNum("Label size (pt)", (int)Settings.MinSmallPt, (int)Settings.MaxSmallPt,
                (int)_working.DriveLabelSize, "Text size of the drive labels.");
            _driveLblBold = AddCheck("Label bold", _working.DriveLabelBold, "Draw the drive labels in bold.");

            GroupHeader("Top CPU / Top RAM");
            _listSize = AddNum("Text size (pt)", (int)Settings.MinSmallPt, (int)Settings.MaxSmallPt,
                (int)_working.ListSize, "Text size of both top-process lists.");
        }

        private void BuildNetwork()
        {
            GroupHeader("Network");
            _netUnit = AddChoice("Units", new string[] { "MB/s  (bytes)", "Mbps  (bits)" },
                _working.NetUnitBytes ? 0 : 1,
                "Bytes is what file managers show; bits is what internet plans are sold in.");
            _netTotalsSize = AddNum("Totals text size (pt)", (int)Settings.MinSmallPt, (int)Settings.MaxSmallPt,
                (int)_working.NetTotalsSize, "Text size of the session download/upload totals.");
            Hint("The NET graph's two colours are on the Graphs page, with its scale.");

            GroupHeader("IP addresses");
            _ipSize = AddNum("Text size (pt)", (int)Settings.MinSmallPt, (int)Settings.MaxSmallPt,
                (int)_working.IpSize, "Text size of the LAN / WAN lines.");
            _ipLanSec = AddNum("LAN refresh (s)", 2, 3600, _working.IpLanRefreshSec,
                "How often the local IP is re-read. Cheap: no network traffic.");
            _extIp = AddCheck("Show public (WAN) IP", _working.ExternalIpEnabled,
                "Look up your public IP over HTTPS (api.ipify.org). Off = no outbound requests at all.");
            _ipWanSec = AddNum("WAN refresh (s)", 30, 86400, _working.IpWanRefreshSec,
                "How often the public IP is looked up. \"Refresh WAN now\" in the menu does it on demand.");
            _showWanCountry = AddCheck("Show country", _working.ShowWanCountry,
                "Show the country of your public IP beneath it (one extra online lookup, ipwho.is).");
            _showWanFlag = AddCheck("Show flag", _working.ShowWanFlag,
                "Show the country's flag next to it (downloads a small image from flagcdn.com once).");
        }

        private void BuildTemperatures()
        {
            GroupHeader("Display");
            _tempUnit = AddChoice("Units", new string[] { "°C  Celsius", "°F  Fahrenheit" },
                _working.TempFahrenheit ? 1 : 0, "Unit used for every temperature LoadView shows.");

            // The threshold is stored in °C whatever the display unit, so the equivalent is shown live
            // rather than converted back and forth (which would drift with every switch).
            bool hotOn = _working.TempHotC > 0;
            _tempHotOn = AddCheck("Highlight when hot", hotOn,
                "Draw a temperature in red once it reaches the threshold below.");
            _tempHot = AddNum("Threshold (°C)", (int)Settings.MinHotC, (int)Settings.MaxHotC,
                hotOn ? (int)_working.TempHotC : 85, "Always entered in °C, whichever unit is displayed.");
            _tempHot.Enabled = hotOn;
            _tempHotEquiv = Label(CtrlX + 100, _tempHot.Top + 3, 90, "", Dim);
            _tempHotOn.CheckedChanged += delegate { _tempHot.Enabled = _tempHotOn.Checked; OnChanged(); };
            _tempHot.ValueChanged += delegate { UpdateHotEquivalent(); };
            _tempUnit.SelectedIndexChanged += delegate { UpdateHotEquivalent(); };
            UpdateHotEquivalent();

            GroupHeader("On the graphs");
            _showCpuTemp = AddCheck("CPU temperature on the CPU graph", _working.ShowCpuTemp,
                "Append the CPU temperature to the CPU graph's header when it is available.");
            _showGpuTemp = AddCheck("GPU temperature on the GPU graph", _working.ShowGpuTemp,
                "Append the GPU temperature to the GPU graph's header when it is available.");
            Hint("GPU temperature needs no driver and works on NVIDIA / AMD / Intel where");
            Hint("the driver reports it. CPU temperature without the option below relies on");
            Hint("the ACPI sensor, which many laptops do not expose at all.");

            GroupHeader("Accurate CPU temperature");
            _accurateDriver = AddCheck("Use the sensor driver", _working.AccurateCpuTempDriver,
                "Reads the true CPU core temperature. On first enable it installs the free, signed "
                + "PawnIO driver (one administrator prompt) and works even with Windows Memory "
                + "Integrity on. After that it starts silently — no more prompts. Off = no driver.");
            Hint("Installs the free, open-source, signed PawnIO driver — one administrator");
            Hint("prompt the first time, silent afterwards. Leave it off to keep LoadView");
            Hint("completely driver-free; the CPU temperature then stays blank on most laptops.");
        }

        private void UpdateHotEquivalent()
        {
            if (_tempHotEquiv == null || _tempUnit == null) return;
            if (_tempUnit.SelectedIndex == 1)
            {
                double f = (double)_tempHot.Value * 9.0 / 5.0 + 32.0;
                _tempHotEquiv.Text = "= " + f.ToString("0") + " °F";
            }
            else _tempHotEquiv.Text = "";
        }

        private void BuildAdvanced()
        {
            GroupHeader("Updates");
            _refreshMs = AddNum("Metrics refresh (ms)", Settings.MinRefreshMs, Settings.MaxRefreshMs,
                _working.RefreshMs, "How often every graph and readout updates. Lower costs more CPU.");
            _refreshMs.Increment = 100;

            GroupHeader("Diagnostics");
            _debugLog = AddCheck("Write debug log", _working.DebugLog,
                "Log to %APPDATA%\\LoadView\\loadview.log. Off by default; the file self-truncates.");

            GroupHeader("Defaults");
            Label(LabelX, _y, 420, "Keep the current configuration as your own defaults, or go back to them.", Dim);
            _y += 30;

            Button save = new Button();
            save.Text = "Save current as defaults";
            save.SetBounds(LabelX, _y, 200, 30);
            StyleButton(save);
            _tips.SetToolTip(save, "Writes defaults.ini, which \"Reset to defaults\" and a fresh start use.");
            save.Click += delegate { SaveAsDefaults(); };
            _panel.Controls.Add(save);

            Button reset = new Button();
            reset.Text = "Reset to defaults";
            reset.SetBounds(LabelX + 210, _y, 160, 30);
            StyleButton(reset);
            _tips.SetToolTip(reset, "Loads your saved defaults, or the built-in ones if you never saved any.");
            reset.Click += delegate { ResetToDefaults(); };
            _panel.Controls.Add(reset);
            _y += 40;
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

            // Nothing in the dialog said that edits land on the overlay straight away, so people
            // hunted for an Apply button that was deliberately removed.
            Label live = new Label();
            live.Text = "Changes apply as you make them.  Cancel restores the previous settings.";
            live.ForeColor = Dim;
            live.SetBounds(16, 18, 420, 20);
            bottom.Controls.Add(live);

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

            // Auto/off is still stored as 0, so the file format is unchanged — the checkboxes only
            // replace the user having to know that.
            _working.CpuColor = _gColor[0].BackColor; _working.CpuMax = GMax(0); _working.CpuAlert = GAlert(0);
            _working.GpuColor = _gColor[1].BackColor; _working.GpuMax = GMax(1); _working.GpuAlert = GAlert(1);
            _working.MemColor = _gColor[2].BackColor; _working.MemMax = GMax(2); _working.MemAlert = GAlert(2);
            _working.DiskColor = _gColor[3].BackColor; _working.DiskMax = GMax(3); _working.DiskAlert = GAlert(3);
            _working.NetMax = GMax(4); _working.NetAlert = GAlert(4);

            _working.NetUnitBytes = (_netUnit.SelectedIndex == 0);
            _working.NetDownColor = _netDownColor.BackColor;
            _working.NetUpColor = _netUpColor.BackColor;
            _working.NetTotalsSize = (float)_netTotalsSize.Value;
            _working.IpLanRefreshSec = (int)_ipLanSec.Value;
            _working.IpWanRefreshSec = (int)_ipWanSec.Value;
            _working.ShowWanCountry = _showWanCountry.Checked;
            _working.ShowWanFlag = _showWanFlag.Checked;

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

            _working.TempFahrenheit = (_tempUnit.SelectedIndex == 1);
            _working.ShowCpuTemp = _showCpuTemp.Checked;
            _working.ShowGpuTemp = _showGpuTemp.Checked;
            _working.TempHotC = _tempHotOn.Checked ? (double)_tempHot.Value : 0.0;
            _working.AccurateCpuTempDriver = _accurateDriver.Checked;

            _working.Opacity = _opacity.Value / 100.0;
            _working.AlwaysOnTop = _top.Checked;
            _working.Locked = _lock.Checked;
            _working.ExternalIpEnabled = _extIp.Checked;
            _working.DebugLog = _debugLog.Checked;
        }

        // 0 keeps its stored meaning: auto-scale, and no alert.
        private double GMax(int i) { return _gAuto[i].Checked ? 0.0 : (double)_gMax[i].Value; }
        private double GAlert(int i) { return _gAlertOn[i].Checked ? (double)_gAlert[i].Value : 0.0; }

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
            n.SetBounds(x, _y, 66, 24);
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

        // A two-or-more-way choice. Used where a checkbox hid what the unchecked state meant:
        // "Network in bytes" and "Show in °F" never said what you got by leaving them off.
        private ComboBox AddChoice(string label, string[] items, int index, string tip)
        {
            RowLabel(label);
            ComboBox c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.FlatStyle = FlatStyle.Flat;
            c.BackColor = FieldBg; c.ForeColor = Ink;
            c.Items.AddRange(items);
            c.SelectedIndex = (index >= 0 && index < items.Length) ? index : 0;
            c.SetBounds(CtrlX, _y, 170, 24);
            c.SelectedIndexChanged += delegate { OnChanged(); };
            if (tip != null) _tips.SetToolTip(c, tip);
            _panel.Controls.Add(c);
            _y += 30;
            return c;
        }

        // Group heading inside a page, so a long list of rows reads as sections rather than a wall.
        private Font _headerFont;

        private void GroupHeader(string text)
        {
            if (_headerFont == null) _headerFont = new Font(Font.FontFamily, Font.Size - 0.5f, FontStyle.Bold);
            _y += 6;
            Label l = Label(LabelX, _y, 300, text.ToUpperInvariant(), Accent);
            l.Font = _headerFont;   // one font for every header, not one per header
            _y += 24;
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
