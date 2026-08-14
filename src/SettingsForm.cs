using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace LoadView
{
    // Category-sidebar settings dialog with instant live preview. Every change is pushed to
    // the overlay via the preview callback; OK keeps it (caller persists), Cancel reverts.
    internal sealed class SettingsForm : Form
    {
        // All from Theme, so the dialog matches the overlay — and so choosing a theme inside the
        // dialog can restyle the dialog itself rather than leaving a dark window claiming to preview
        // a light one.
        private static Color Bg { get { return Theme.DialogBack; } }
        private static Color NavBg { get { return Theme.NavBack; } }
        private static Color Ink { get { return Theme.Text; } }
        private static Color Dim { get { return Theme.Dim; } }
        private static Color Accent { get { return Theme.Accent; } }
        private static Color FieldBg { get { return Theme.FieldBack; } }

        private Settings _working;
        private readonly Action<Settings> _preview;
        // Supplies the sensors that are readable right now, so the tile list can show real values
        // instead of bare identifiers. Null in tests, which the list handles.
        private readonly Func<SensorReading[]> _sensors;
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
            _driveLblSize, _listSize, _ipSize, _netTotalsSize, _ipLanSec, _ipWanSec, _tempHot,
            _tileH, _tileCols, _tileLabel, _tileValue;
        private CheckedListBox _tiles, _fans;
        private Label _fansEmptyHint;
        private CheckBox _seconds, _dateBold, _dayBold, _driveLblBold, _extIp, _top, _lock, _startup, _debugLog,
            _accurateDriver, _showWanCountry, _showWanFlag, _tempHotOn, _wideSensors;
        private ComboBox _netUnit, _tempUnit, _theme;
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
            : this(working, preview, null) { }

        public SettingsForm(Settings working, Action<Settings> preview, Func<SensorReading[]> sensors)
        {
            _working = working;
            _preview = preview;
            _sensors = sensors;

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
            AddPage("Fans", BuildFans);
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
            _theme = AddChoice("Theme", new string[] { "Follow system", "Dark", "Light" },
                _working.Theme == ThemeMode.Dark ? 1 : (_working.Theme == ThemeMode.Light ? 2 : 0),
                "Follow system tracks the Windows app theme, including when you switch it.");
            _theme.SelectedIndexChanged += delegate { RestyleForTheme(); };

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
            b.FlatAppearance.BorderColor = Theme.Border;
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
            GroupHeader("Tiles");
            Hint("Check the components to show as tiles.  ▲ ▼ sets their order.");
            BuildTileList();

            _tileH = AddNum("Tile size (px)", 24, 120, _working.TileHeight,
                "Height of each tile; the width follows so they stay roughly square. Shared with Fans.");
            _tileCols = AddNum("Tiles per row", 0, 12, _working.TileColumns,
                "0 = fit as many as the window width allows. Shared with Fans.");
            _tileLabel = AddNum("Label size (pt)", 6, 24, (int)_working.TileLabelSize,
                "Text size of the tile labels. Shared with Fans.");
            _tileValue = AddNum("Reading size (pt)", 7, 40, (int)_working.TileValueSize,
                "Text size of the readings. Shared with Fans.");

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

            Hint("Disks need no driver; GPU works where the graphics driver reports it.");

            GroupHeader("Accurate CPU temperature");
            _accurateDriver = AddCheck("Use the sensor driver", _working.AccurateCpuTempDriver,
                "Reads the true CPU core temperature. On first enable it installs the free, signed "
                + "PawnIO driver (one administrator prompt) and works even with Windows Memory "
                + "Integrity on. After that it starts silently — no more prompts. Off = no driver.");
            Hint("Installs the free, signed PawnIO driver: one administrator prompt, then");
            Hint("silent. Without it the CPU temperature stays blank on most laptops.");
        }

        // The sensor set is only known at runtime, so the list is built from what is readable now,
        // with each entry showing its current value — that is the only way to tell two disks apart.
        // A sensor that is saved but not present right now stays listed and marked, so unplugging a
        // drive does not quietly discard the choice.
        private void BuildTileList()
        {
            Label unused;
            _tiles = BuildSensorList(_working.TempTiles, SensorKind.Temperature, out unused);
        }

        private CheckedListBox BuildSensorList(List<string> chosen, SensorKind kind, out Label emptyHint)
        {
            CheckedListBox box = new CheckedListBox();
            box.BackColor = FieldBg;
            box.ForeColor = Ink;
            box.BorderStyle = BorderStyle.FixedSingle;
            box.IntegralHeight = false;
            box.CheckOnClick = true;
            box.SetBounds(LabelX, _y, 300, 108);

            SensorReading[] found = _sensors != null ? _sensors() : new SensorReading[0];
            List<string> order = new List<string>(chosen);
            // Anything discovered but not in the saved order goes on the end, so new hardware shows
            // up rather than staying invisible.
            for (int i = 0; i < found.Length; i++)
                if (found[i].Kind == kind && !order.Contains(found[i].Id))
                    order.Add(found[i].Id);

            bool showAll = chosen.Count == 0;
            foreach (string id in order)
            {
                SensorReading? match = null;
                for (int i = 0; i < found.Length; i++) if (found[i].Id == id) { match = found[i]; break; }
                int idx = box.Items.Add(new TileItem(id, match));
                box.SetItemChecked(idx, showAll || chosen.Contains(id));
            }
            box.ItemCheck += delegate { BeginInvoke(new MethodInvoker(OnChanged)); };
            _panel.Controls.Add(box);

            Button up = SmallButton("▲", box.Right + 10, _y);
            up.Click += delegate { MoveInList(box, -1); };
            _tips.SetToolTip(up, "Move the selected tile left");
            _panel.Controls.Add(up);
            Button down = SmallButton("▼", box.Right + 10, _y + 36);
            down.Click += delegate { MoveInList(box, 1); };
            _tips.SetToolTip(down, "Move the selected tile right");
            _panel.Controls.Add(down);
            _y += box.Height + 4;

            // Say why the list is empty instead of showing a blank box.
            emptyHint = null;
            if (box.Items.Count == 0)
                emptyHint = Hint(kind == SensorKind.Fan
                    ? "No fan speeds are readable right now."
                    : "No temperature sensors are readable right now.");
            _y += 4;
            return box;
        }

        private void MoveInList(CheckedListBox box, int delta)
        {
            int i = box.SelectedIndex;
            if (i < 0) return;
            int j = i + delta;
            if (j < 0 || j >= box.Items.Count) return;
            object item = box.Items[i];
            bool chk = box.GetItemChecked(i);
            box.Items.RemoveAt(i);
            box.Items.Insert(j, item);
            box.SetItemChecked(j, chk);
            box.SelectedIndex = j;
            OnChanged();
        }

        // Read a checked list back into an ID list; all-ticked becomes empty, i.e. "show whatever is
        // there", so hardware added later appears by itself.
        private static List<string> ListFrom(CheckedListBox box)
        {
            List<string> ids = new List<string>();
            bool all = true;
            for (int i = 0; i < box.Items.Count; i++)
            {
                if (box.GetItemChecked(i)) ids.Add(((TileItem)box.Items[i]).Id);
                else all = false;
            }
            return all ? new List<string>() : ids;
        }

        private sealed class TileItem
        {
            public readonly string Id;
            private readonly string _text;

            public TileItem(string id, SensorReading? found)
            {
                Id = id;
                if (found.HasValue)
                {
                    SensorReading r = found.Value;
                    _text = r.Label + "   " + r.Value.ToString("0", CultureInfo.InvariantCulture)
                        + (r.Kind == SensorKind.Fan ? " rpm" : " °C")
                        + (string.IsNullOrEmpty(r.Detail) ? "" : "   (" + r.Detail + ")");
                }
                else _text = id + "   (not present)";
            }

            public override string ToString() { return _text; }
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

        private void BuildFans()
        {
            GroupHeader("Tiles");
            Hint("Check the fans to show.  ▲ ▼ sets their order.");
            _fans = BuildSensorList(_working.FanTiles, SensorKind.Fan, out _fansEmptyHint);
            Hint("Tile and text sizes are shared with the Temperatures page.");

            GroupHeader("Where fan speeds come from");
            Hint("Fan speeds live on the motherboard's controller chip, so they need the");
            Hint("sensor driver plus the switch below. Many laptops expose none even then;");
            Hint("the section stays hidden when there is nothing to show.");
            _wideSensors = AddCheck("Read chipset + fan sensors", _working.WideSensors,
                "Lets the reader probe the motherboard and its controller chip, which is what exposes "
                + "fan speeds and chipset temperatures. Needs the sensor driver from the Temperatures "
                + "page. Separate switch because this probes more of your hardware than a CPU "
                + "temperature does.");
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

        private const int BtnW = 90, BtnH = 32, BtnGap = 10;
        private Button _ok, _cancel;
        private Label _liveHint;
        private Panel _bottom;

        // Positioned from the panel's real width instead of anchors, and re-run on resize.
        //
        // The panel is docked Bottom but sits beside the 150 px nav, so it is 510 wide, not the form's
        // 660 — and right-anchored buttons only reach their true position once layout runs. Trusting
        // either the form width or a pre-layout Left is what put the footer text underneath OK.
        private void LayoutBottom()
        {
            if (_bottom == null || _ok == null) return;
            int right = _bottom.ClientSize.Width - 16;
            _ok.SetBounds(right - BtnW * 2 - BtnGap, 10, BtnW, BtnH);
            _cancel.SetBounds(right - BtnW, 10, BtnW, BtnH);
            if (_liveHint != null)
            {
                int w = _ok.Left - _liveHint.Left - 12;
                _liveHint.Width = w > 60 ? w : 60;
            }
        }

        private void BuildButtons(Panel bottom)
        {
            _bottom = bottom;
            bottom.Resize += delegate { LayoutBottom(); };

            Button ok = new Button();
            ok.Text = "OK";
            ok.SetBounds(0, 10, BtnW, BtnH);
            StyleButton(ok);
            ok.Click += delegate { CommitToWorking(); Startup.SetEnabled(_startup.Checked); DialogResult = DialogResult.OK; };
            bottom.Controls.Add(ok);
            _ok = ok;

            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(0, 10, BtnW, BtnH);
            StyleButton(cancel);
            bottom.Controls.Add(cancel);
            _cancel = cancel;

            // Nothing in the dialog said that edits land on the overlay straight away, so people
            // hunted for an Apply button that was deliberately removed.
            //
            // The width has to be worked out *after* layout, not at construction time. This panel is
            // docked Bottom but sits beside the 150 px nav, so it is 510 wide rather than the form's
            // 660 — and the right-anchored buttons only move to their real position when the panel is
            // laid out. Sizing the label against ok.Left here in the constructor used the pre-layout
            // 454 instead of the actual 304, which is how the text ended up running under the button.
            Label live = new Label();
            // 246 px measured against the 276 the panel leaves before OK — chosen so it reads as a
            // whole sentence rather than being ellipsised.
            live.Text = "Changes apply live.  Cancel reverts them.";
            live.ForeColor = Dim;
            live.AutoSize = false;
            live.AutoEllipsis = true;   // a longer wording ellipsises rather than sliding under OK
            live.SetBounds(16, 18, 200, 20);
            bottom.Controls.Add(live);
            _liveHint = live;

            LayoutBottom();   // and again from Resize once docking gives the panel its real width

            AcceptButton = ok;
            CancelButton = cancel;
        }

        // Repaint the dialog in the newly chosen theme. OnChanged has already pushed the setting to
        // the overlay, which resolved Theme — this walks the controls we styled at build time and
        // gives them the new colours, so the preview is honest rather than "the overlay is light and
        // the window telling you so is still dark".
        private void RestyleForTheme()
        {
            BackColor = Bg;
            ForeColor = Ink;
            if (_nav != null) { _nav.BackColor = NavBg; _nav.ForeColor = Ink; _nav.Invalidate(); }
            if (_host != null) _host.BackColor = Bg;
            foreach (Control c in Controls) Restyle(c);
            Invalidate(true);
        }

        private void Restyle(Control c)
        {
            if (c is Panel) c.BackColor = Bg;
            else if (c is Label)
            {
                // Group headers and hints are dim/accent; body labels are ink. Distinguish by the
                // colour they were given, since that is the only thing that marks them apart.
                Color f = c.ForeColor;
                if (SameRgb(f, Theme.Accent) || IsAccentish(f)) c.ForeColor = Theme.Accent;
                else if (IsDimish(f)) c.ForeColor = Dim;
                else c.ForeColor = Ink;
            }
            else if (c is NumericUpDown || c is ComboBox || c is CheckedListBox || c is ListBox)
            { c.BackColor = FieldBg; c.ForeColor = Ink; }
            else if (c is CheckBox) c.ForeColor = Ink;
            else if (c is Button)
            {
                // Colour swatches keep their colour; only chrome buttons are restyled.
                Button b = (Button)c;
                if (b.Text.Length > 0) StyleButton(b);
                else b.FlatAppearance.BorderColor = Theme.Border;
            }
            foreach (Control k in c.Controls) Restyle(k);
        }

        private static bool SameRgb(Color a, Color b) { return a.R == b.R && a.G == b.G && a.B == b.B; }
        private static bool IsAccentish(Color c) { return c.B > c.R + 40 && c.B > 120; }
        private static bool IsDimish(Color c)
        {
            int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
            return max - min < 24 && max > 90 && max < 190;   // a grey, neither ink nor black
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

            _working.TempTiles = ListFrom(_tiles);
            _working.FanTiles = ListFrom(_fans);
            _working.WideSensors = _wideSensors.Checked;
            _working.TileHeight = (int)_tileH.Value;
            _working.TileColumns = (int)_tileCols.Value;
            _working.TileLabelSize = (float)_tileLabel.Value;
            _working.TileValueSize = (float)_tileValue.Value;

            _working.TempFahrenheit = (_tempUnit.SelectedIndex == 1);
            _working.TempHotC = _tempHotOn.Checked ? (double)_tempHot.Value : 0.0;
            _working.AccurateCpuTempDriver = _accurateDriver.Checked;

            _working.Theme = _theme.SelectedIndex == 1 ? ThemeMode.Dark
                : (_theme.SelectedIndex == 2 ? ThemeMode.Light : ThemeMode.System);
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
            b.FlatAppearance.BorderColor = Theme.Border;
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
            b.BackColor = Theme.ButtonBack;
            b.ForeColor = Theme.Text;
            b.FlatAppearance.BorderColor = Theme.Border;
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
