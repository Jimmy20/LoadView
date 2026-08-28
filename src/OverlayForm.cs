using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LoadView
{
    // The draggable, semi-transparent overlay. Sections (clock, metric graphs, net totals,
    // top processes, drives, IP, date/weekday) are laid out in a user-defined order; each is
    // sizable/colorable/toggleable from Settings. Can float on top or behave like a normal
    // (coverable) window.
    internal sealed class OverlayForm : Form
    {
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        private const int WM_DPICHANGED = 0x02E0;
        private const int WM_DISPLAYCHANGE = 0x007E;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const double GiB = 1024.0 * 1024.0 * 1024.0;

        private readonly MetricsSampler _sampler;
        private readonly ProcessSampler _procs;
        private readonly SystemInfoProvider _sysinfo;
        private readonly Timer _timer;
        private Settings _settings;

        private ClockPanel _clock;
        private GraphPanel _cpu, _gpu, _ram, _disk, _net;
        private NetTotalsPanel _netTotals;
        private ListPanel _topCpu, _topRam;
        private DrivesPanel _drives;
        private TilePanel _tempTiles, _fanTiles;
        private string _tileSig = "";     // relayout only when the set of tiles changes
        private IpPanel _ip;
        private readonly Dictionary<string, Image> _flagCache = new Dictionary<string, Image>();
        private FooterPanel _footer;

        private ContextMenuStrip _menu;
        private ToolStripMenuItem _lockItem;
        private ToolStripMenuItem _topItem;
        private NotifyIcon _tray;

        private bool _dragging;
        private Point _dragMouseStart;
        private Point _dragFormStart;

        private string _driveSig = "";
        private string _activeSig = "";
        private bool _lastNetUnitBytes;
        private double _totalDownBytes, _totalUpBytes;

        public OverlayForm()
        {
            _settings = Settings.Load();
            Log.Enabled = _settings.DebugLog;
            Startup.RemoveLegacyRunKey(); // clean up the HKCU\Run value older builds wrote
            _lastNetUnitBytes = _settings.NetUnitBytes;
            _sampler = new MetricsSampler();
            _procs = new ProcessSampler();
            _sysinfo = new SystemInfoProvider();

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(12, 12, 14);
            Font = new Font("Segoe UI", 9f);
            DoubleBuffered = true;
            Text = "LoadView";

            BuildPanels();
            BuildTray();
            ContextMenuStrip = SharedMenu();
            WireDrag(this);

            _timer = new Timer();
            _timer.Interval = 1000;
            _timer.Tick += OnTick;
        }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= WS_EX_TOOLWINDOW; return cp; }
        }

        // ---------- panels ----------

        private void BuildPanels()
        {
            _clock = new ClockPanel();
            _cpu  = NewGraph("CPU",  false);
            _gpu  = NewGraph("GPU",  false);
            _ram  = NewGraph("MEM",  false);
            _disk = NewGraph("DISK", false);
            _net  = NewGraph("NET",  true);
            _net.Percent = false;

            _netTotals = new NetTotalsPanel();
            _topCpu = new ListPanel(); _topCpu.Header = "TOP CPU"; _topCpu.IsBytes = false;
            _topRam = new ListPanel(); _topRam.Header = "TOP RAM"; _topRam.IsBytes = true;
            _drives = new DrivesPanel();
            _ip = new IpPanel();
            _footer = new FooterPanel();
            _tempTiles = new TilePanel();
            _tempTiles.Header = "TEMPERATURES";
            _fanTiles = new TilePanel();
            _fanTiles.Header = "FANS (RPM)";

            Control[] all = new Control[]
            {
                _clock, _tempTiles, _fanTiles, _cpu, _gpu, _ram, _disk, _net,
                _netTotals, _topCpu, _topRam, _drives, _ip, _footer
            };
            foreach (Control c in all)
            {
                c.ContextMenuStrip = SharedMenu();
                WireDrag(c);
                Controls.Add(c);
            }
        }

        private static GraphPanel NewGraph(string title, bool two)
        {
            GraphPanel p = new GraphPanel(two);
            p.Title = title;
            return p;
        }

        private Control PanelFor(string key)
        {
            switch (key)
            {
                case Settings.SecClock: return _clock;
                case Settings.SecCpu: return _cpu;
                case Settings.SecGpu: return _gpu;
                case Settings.SecMem: return _ram;
                case Settings.SecDisk: return _disk;
                case Settings.SecNet: return _net;
                case Settings.SecNetTotals: return _netTotals;
                case Settings.SecTopCpu: return _topCpu;
                case Settings.SecTopRam: return _topRam;
                case Settings.SecDrives: return _drives;
                case Settings.SecIp: return _ip;
                case Settings.SecFooter: return _footer;
                case Settings.SecTemps: return _tempTiles;
                case Settings.SecFans: return _fanTiles;
            }
            return null;
        }

        // ---------- menus / tray ----------

        private ContextMenuStrip SharedMenu()
        {
            if (_menu != null) return _menu;
            _menu = new ContextMenuStrip();
            _lockItem = new ToolStripMenuItem("Lock");
            _lockItem.Click += delegate { ToggleLock(); };
            _menu.Items.Add(_lockItem);
            _topItem = new ToolStripMenuItem("Always on top");
            _topItem.Click += delegate { ToggleAlwaysOnTop(); };
            _menu.Items.Add(_topItem);
            _menu.Items.Add("Refresh WAN now", null, delegate { _sysinfo.RefreshWanNow(); });
            _menu.Items.Add("Reset position", null, delegate { ResetPosition(); });
            _menu.Items.Add("Settings...", null, delegate { OpenSettings(); });
            _menu.Items.Add("Contact me", null, delegate { OpenContact(); });
            _menu.Items.Add("About", null, delegate { OpenAbout(); });
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("Exit", null, delegate { Close(); });
            return _menu;
        }

        private void BuildTray()
        {
            _tray = new NotifyIcon();
            _tray.Text = "LoadView";
            _tray.Icon = LoadTrayIcon();
            _tray.Visible = true;
            _tray.MouseClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ShowToFront();
            };

            ContextMenuStrip tm = new ContextMenuStrip();
            tm.Items.Add("Show / Hide", null, delegate { ToggleVisible(); });
            tm.Items.Add(new ToolStripSeparator());
            tm.Items.Add("Refresh WAN now", null, delegate { _sysinfo.RefreshWanNow(); });
            tm.Items.Add("Reset position", null, delegate { ResetPosition(); });
            tm.Items.Add("Settings...", null, delegate { OpenSettings(); });
            tm.Items.Add("Contact me", null, delegate { OpenContact(); });
            tm.Items.Add("About", null, delegate { OpenAbout(); });
            tm.Items.Add(new ToolStripSeparator());
            tm.Items.Add("Exit", null, delegate { Close(); });
            _tray.ContextMenuStrip = tm;
        }

        // Prefer the exe's own embedded icon so the tray and the .exe show the same glyph.
        private static Icon LoadTrayIcon()
        {
            try
            {
                Icon ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (ico != null) return ico;
            }
            catch { }
            return MakeIcon();
        }

        private static Icon MakeIcon()
        {
            try
            {
                using (Bitmap bmp = new Bitmap(32, 32))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    using (SolidBrush b1 = new SolidBrush(Color.FromArgb(0x4F, 0x8C, 0xFF)))
                    using (SolidBrush b2 = new SolidBrush(Color.FromArgb(0x36, 0xC7, 0x9B)))
                    using (SolidBrush b3 = new SolidBrush(Color.FromArgb(0xFF, 0x9F, 0x40)))
                    {
                        g.FillRectangle(b1, 4, 16, 6, 12);
                        g.FillRectangle(b2, 13, 9, 6, 19);
                        g.FillRectangle(b3, 22, 4, 6, 24);
                    }
                    IntPtr h = bmp.GetHicon();
                    try { return (Icon)Icon.FromHandle(h).Clone(); }
                    finally { DestroyIcon(h); }
                }
            }
            catch { return SystemIcons.Application; }
        }

        // ---------- dragging ----------

        private void WireDrag(Control c)
        {
            c.MouseDown += DragDown;
            c.MouseMove += DragMove;
            c.MouseUp += DragUp;
        }

        private void DragDown(object sender, MouseEventArgs e)
        {
            if (_settings.Locked) return;
            if (e.Button == MouseButtons.Left)
            {
                _dragging = true;
                _dragMouseStart = Control.MousePosition;
                _dragFormStart = Location;
            }
        }

        private void DragMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            Point now = Control.MousePosition;
            Location = new Point(_dragFormStart.X + (now.X - _dragMouseStart.X),
                                 _dragFormStart.Y + (now.Y - _dragMouseStart.Y));
        }

        private void DragUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _dragging)
            {
                _dragging = false;
                SaveSettings();
            }
        }

        // ---------- commands ----------

        private void OpenSettings()
        {
            Settings original = _settings.Clone();
            using (SettingsForm f = new SettingsForm(_settings.Clone(), PreviewSettings, _sampler.Sensors))
            {
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    PreviewSettings(f.Result);
                    SaveSettings();          // persist only on OK
                }
                else
                {
                    PreviewSettings(original); // Cancel reverts the live preview
                }
            }
        }

        private void OpenAbout()
        {
            using (AboutForm a = new AboutForm())
                a.ShowDialog(this);
        }

        // Open the default mail client to contact the author.
        private void OpenContact()
        {
            try { System.Diagnostics.Process.Start("mailto:Jimmy20@seznam.cz?subject=LoadView"); }
            catch (Exception ex) { Log.Write("mailto", ex); }
        }

        private void ToggleLock()
        {
            _settings.Locked = !_settings.Locked;
            if (_lockItem != null) _lockItem.Checked = _settings.Locked;
            SaveSettings();
        }

        private void ToggleAlwaysOnTop()
        {
            _settings.AlwaysOnTop = !_settings.AlwaysOnTop;
            TopMost = _settings.AlwaysOnTop;
            if (_settings.AlwaysOnTop) AssertTopmost();
            else if (IsHandleCreated)
                SetWindowPos(Handle, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            if (_topItem != null) _topItem.Checked = _settings.AlwaysOnTop;
            SaveSettings();
        }

        private void ToggleVisible()
        {
            Visible = !Visible;
            if (Visible && _settings.AlwaysOnTop) { TopMost = true; AssertTopmost(); }
        }

        // Bring the overlay to the foreground, even when it's a normal (non-topmost) window
        // or currently hidden/covered.
        private void ShowToFront()
        {
            if (!Visible) Visible = true;
            if (!IsHandleCreated) return;
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            if (!_settings.AlwaysOnTop)
                SetWindowPos(Handle, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            SetForegroundWindow(Handle);
        }

        private void ResetPosition()
        {
            DoLayout();
            Location = DefaultLocation(Size);
            ApplyRegion();
            SaveSettings();
        }

        // Load (and cache) the flag PNG for a country code; null until the provider has downloaded it.
        private Image FlagImage(string cc)
        {
            if (string.IsNullOrEmpty(cc)) return null;
            Image img;
            if (_flagCache.TryGetValue(cc, out img)) return img;
            string path = SystemInfoProvider.FlagPath(cc);
            if (path == null || !System.IO.File.Exists(path)) return null;   // retry next tick once it exists
            try
            {
                using (System.IO.FileStream fs = System.IO.File.OpenRead(path))
                using (Image tmp = Image.FromStream(fs))
                    img = new Bitmap(tmp);   // copy so the file isn't locked
                _flagCache[cc] = img;
                return img;
            }
            catch { return null; }
        }

        // ---------- apply settings ----------

        // Apply settings to the overlay without persisting (used for live preview from the
        // Settings dialog; disk is written only on OK via SaveSettings).
        private void PreviewSettings(Settings s)
        {
            _settings = s;
            ApplyVisuals();
            DoLayout();
            if (_settings.AlwaysOnTop) AssertTopmost();
        }

        private void ApplyVisuals()
        {
            ApplyTheme();
            Opacity = ClampOpacity(_settings.Opacity);
            TopMost = _settings.AlwaysOnTop;
            Log.Enabled = _settings.DebugLog;

            int ms = _settings.RefreshMs;
            if (ms < 200) ms = 200; else if (ms > 10000) ms = 10000;
            if (_timer != null) _timer.Interval = ms;
            if (_sampler != null) _sampler.IntervalMs = ms;

            _clock.SizePt = _settings.ClockSize;
            _clock.Ink = _settings.ClockColor;

            _footer.DateSizePt = _settings.DateSize;
            _footer.DaySizePt = _settings.DaySize;
            _footer.DateBold = _settings.DateBold;
            _footer.DayBold = _settings.DayBold;
            _footer.DateInk = _settings.DateColor;
            _footer.DayInk = _settings.DayColor;

            _drives.LabelSize = _settings.DriveLabelSize;
            _drives.LabelBold = _settings.DriveLabelBold;

            _topCpu.TextSize = _settings.ListSize;
            _topRam.TextSize = _settings.ListSize;
            _ip.TextSize = _settings.IpSize;

            _cpu.Accent = _settings.CpuColor;  _cpu.FixedMax = _settings.CpuMax;  _cpu.AlertThreshold = _settings.CpuAlert;
            _gpu.Accent = _settings.GpuColor;  _gpu.FixedMax = _settings.GpuMax;  _gpu.AlertThreshold = _settings.GpuAlert;
            _ram.Accent = _settings.MemColor;  _ram.FixedMax = _settings.MemMax;  _ram.AlertThreshold = _settings.MemAlert;
            _disk.Accent = _settings.DiskColor; _disk.FixedMax = _settings.DiskMax; _disk.AlertThreshold = _settings.DiskAlert;
            _net.Accent = _settings.NetDownColor; _net.Accent2 = _settings.NetUpColor;
            _net.FixedMax = _settings.NetMax;  _net.AlertThreshold = _settings.NetAlert;
            _net.MinScale = _settings.NetUnitBytes ? 0.1 : 1.0;

            _netTotals.TextSize = _settings.NetTotalsSize;
            _netTotals.DownColor = _settings.NetDownColor;
            _netTotals.UpColor = _settings.NetUpColor;

            if (_lastNetUnitBytes != _settings.NetUnitBytes)
            {
                _lastNetUnitBytes = _settings.NetUnitBytes;
                _net.ClearHistory();
            }

            TilePanel[] tiles = new TilePanel[] { _tempTiles, _fanTiles };
            for (int i = 0; i < tiles.Length; i++)
            {
                tiles[i].TilePx = _settings.TileHeight;
                tiles[i].ColumnsWanted = _settings.TileColumns;
                tiles[i].LabelSize = _settings.TileLabelSize;
                tiles[i].ValueSize = _settings.TileValueSize;
                tiles[i].Fahrenheit = _settings.TempFahrenheit;
            }
            // Fans have no "too hot", so only the temperature tiles get thresholds.
            _tempTiles.HotCpuC = _settings.TempHotCpuC;
            _tempTiles.HotGpuC = _settings.TempHotGpuC;
            _tempTiles.HotDiskC = _settings.TempHotDiskC;
            _tempTiles.HotOtherC = _settings.TempHotOtherC;

            // The helper reads this on startup, so a change only takes effect when it restarts —
            // hence clearing the heartbeat to make it exit and come back.
            //
            // The first pass always writes the flag, even when the setting matches the field's
            // initial value: otherwise a flag file left behind by a crash (or by an earlier session
            // that had the option on) would keep the helper probing hardware the settings say to
            // leave alone.
            if (!_wideSensorsSynced || _wideSensorsApplied != _settings.WideSensors)
            {
                _wideSensorsSynced = true;
                _wideSensorsApplied = _settings.WideSensors;
                TempIpc.SetWideSensors(_settings.WideSensors);
                if (_helperEngaged)
                {
                    TempIpc.ClearHeartbeat();
                    _lastTaskRunUtc = DateTime.MinValue;   // let OnTick start it again promptly
                    if (!_settings.WideSensors) _sampler.ClearExtraSensors();
                }
            }

            _ip.ShowWan = _settings.ExternalIpEnabled;
            _sysinfo.ExternalIpEnabled = _settings.ExternalIpEnabled;
            _sysinfo.LanRefreshSec = _settings.IpLanRefreshSec;
            _sysinfo.WanRefreshSec = _settings.IpWanRefreshSec;
            _ip.ShowCountry = _settings.ShowWanCountry;
            _ip.ShowFlag = _settings.ShowWanFlag;
            _sysinfo.GeoEnabled = _settings.ShowWanCountry || _settings.ShowWanFlag;
            _sysinfo.FlagEnabled = _settings.ShowWanFlag;

            foreach (string key in Settings.AllSections)
            {
                Control c = PanelFor(key);
                if (c != null) c.Visible = _settings.GetShow(key);
            }

            if (_lockItem != null) _lockItem.Checked = _settings.Locked;
            if (_topItem != null) _topItem.Checked = _settings.AlwaysOnTop;

            UpdateHelper();
        }

        // ---------- accurate CPU temp helper (opt-in, PawnIO + scheduled task) ----------

        private bool _helperEngaged;      // driver path active this session
        private bool _setupPrompted;      // asked to install PawnIO this session
        private bool _pawnReady;          // cached: PawnIO installed + task registered
        private DateTime _lastReadyCheckUtc = DateTime.MinValue;
        private DateTime _lastTaskRunUtc = DateTime.MinValue;
        private DateTime _lastHeartbeatUtc = DateTime.MinValue;
        private DateTime _lastTempReadUtc = DateTime.MinValue;
        private int _readyChecks;         // drives the Ready() back-off
        private bool _wideSensorsApplied; // last value pushed to the helper's request flag
        private bool _wideSensorsSynced;  // false until the flag file has been made to match once

        // How old a published reading may be before it is ignored. Generous on purpose: the reader
        // publishes every ~2 s but can miss a cycle, and a tile that disappears for a few seconds is
        // worse than one showing a value a few seconds old.
        private const double SensorMaxAgeSec = 30.0;

        // Engage/disengage the driver path to match the AccurateCpuTempDriver setting.
        private void UpdateHelper()
        {
            if (_settings.AccurateCpuTempDriver)
            {
                if (!_helperEngaged)
                {
                    _helperEngaged = true;
                    TempIpc.WriteHeartbeat();
                    EngageDriver();
                }
            }
            else if (_helperEngaged)
            {
                _helperEngaged = false;
                TempIpc.ClearHeartbeat();   // helper self-exits within a few seconds
            }
        }

        private void EngageDriver()
        {
            _pawnReady = PawnIoSetup.Ready();
            _lastReadyCheckUtc = DateTime.UtcNow;
            if (_pawnReady)
            {
                PawnIoSetup.RunHelperTask();   // no UAC — runs elevated via the task
                _lastTaskRunUtc = DateTime.UtcNow;
            }
            else
            {
                PromptAndSetup();
            }
        }

        // One-time, one-UAC setup: install the PawnIO driver, stage the reader into an admin-only
        // folder, and register the SYSTEM task that runs it.
        private void PromptAndSetup()
        {
            if (_setupPrompted) return;
            _setupPrompted = true;

            // An existing staged copy means this is a re-run after an app update, not a first install.
            bool update = System.IO.File.Exists(TempIpc.StagedExePath());
            string msg = update
                ? "LoadView has been updated, so the accurate CPU temperature needs its one-time "
                  + "setup again (the reader that runs with system rights has to be refreshed).\r\n\r\n"
                  + "Windows will ask for administrator permission once. Run it now?"
                : "To show the real CPU temperature, LoadView needs two things: the small hardware-"
                  + "sensor driver PawnIO (free, open-source, digitally signed) and a reader that runs "
                  + "with system rights — the CPU's temperature register cannot be read without "
                  + "them.\r\n\r\nWindows will ask for administrator permission once. After that the "
                  + "temperature appears silently on every launch, with no further prompts, and it "
                  + "keeps working even if your Windows account is not an administrator. It also works "
                  + "with Windows Memory Integrity turned on.\r\n\r\nSet it up now?";

            DialogResult r = MessageBox.Show(this, msg, "LoadView — accurate CPU temperature",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;

            // Fetch the sensor library here, as the logged-on user: this process is the one that has
            // the proxy credentials. The elevated side verifies the hash, so it isn't trusting us.
            string zip = TempIpc.DownloadLhmZipAsUser();
            try
            {
                string args = "--temp-setup";
                if (!string.IsNullOrEmpty(zip)) args += " \"" + zip + "\"";
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(
                    Application.ExecutablePath, args);
                psi.UseShellExecute = true;
                psi.Verb = "runas";   // one UAC: installs PawnIO + stages the reader + registers the task
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex) { Log.Write("temp-setup launch (UAC declined?)", ex); }
        }

        // ---------- sizing / layout ----------

        private float Scale() { return DeviceDpi / 96f; }

        private void DoLayout()
        {
            float s = Scale();
            int w = (int)(_settings.Width * s);
            int gap = Math.Max(1, (int)(1 * s));
            int graphH = (int)(_settings.GraphHeight * s);

            _drives.HeaderPx = (int)(18 * s);
            _drives.DriveRowPx = (int)(_settings.DriveRowHeight * s);
            int driveCount = _drives.Drives != null ? _drives.Drives.Length : 0;

            int y = 0;
            foreach (string key in _settings.Order)
            {
                if (!_settings.GetShow(key)) continue;
                Control c = PanelFor(key);
                if (c == null) continue;
                int h = HeightFor(key, graphH, driveCount);
                // A section can ask for no height at all (the tile panel does when it has nothing to
                // show); treat that exactly like being hidden, or it would leave an empty gap.
                if (h <= 0) { c.Visible = false; continue; }
                c.Visible = true;
                c.SetBounds(0, y, w, h);
                y += h + gap;
            }
            ClientSize = new Size(w, y > 0 ? y - gap : 1);
        }

        private int HeightFor(string key, int graphH, int driveCount)
        {
            switch (key)
            {
                case Settings.SecClock: return _clock.PreferredHeight();
                case Settings.SecCpu:
                case Settings.SecGpu:
                case Settings.SecMem:
                case Settings.SecDisk:
                case Settings.SecNet: return graphH;
                case Settings.SecNetTotals: return _netTotals.PreferredHeight();
                case Settings.SecTopCpu: return _topCpu.PreferredHeight();
                case Settings.SecTopRam: return _topRam.PreferredHeight();
                case Settings.SecDrives: return _drives.ContentHeight(driveCount);
                case Settings.SecIp: return _ip.PreferredHeight();
                case Settings.SecFooter: return _footer.PreferredHeight();
                case Settings.SecTemps: return _tempTiles.ContentHeight();   // 0 when nothing is readable
                case Settings.SecFans: return _fanTiles.ContentHeight();
            }
            return graphH;
        }

        private Point DefaultLocation(Size sz)
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int margin = (int)(12 * Scale());
            return new Point(wa.Right - sz.Width - margin, wa.Top + margin);
        }

        private static bool IsOnScreen(Rectangle r)
        {
            Rectangle vs = SystemInformation.VirtualScreen;
            Rectangle handle = new Rectangle(r.Left, r.Top, Math.Min(40, r.Width), Math.Min(40, r.Height));
            return vs.IntersectsWith(r) && vs.Contains(handle);
        }

        // Signature of the current display layout (resolution + multi-monitor arrangement).
        private static string CurrentSig()
        {
            Rectangle v = SystemInformation.VirtualScreen;
            return v.Left + "," + v.Top + "," + v.Width + "," + v.Height;
        }

        // Restore the position remembered for the current display layout, else the legacy
        // single position (if it fits), else the default top-right.
        private void RestorePosition()
        {
            _activeSig = CurrentSig();
            int px, py;
            if (_settings.TryGetPos(_activeSig, out px, out py) &&
                IsOnScreen(new Rectangle(px, py, Width, Height)))
            {
                Location = new Point(px, py);
                return;
            }
            if (_settings.HasPosition && IsOnScreen(new Rectangle(_settings.X, _settings.Y, Width, Height)))
            {
                Location = new Point(_settings.X, _settings.Y);
                return;
            }
            Location = DefaultLocation(Size);
        }

        private void ApplyRegion()
        {
            if (!IsHandleCreated) return;
            int radius = (int)(10 * Scale());
            IntPtr rgn = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, radius, radius);
            Region = Region.FromHrgn(rgn);
            DeleteObject(rgn);
        }

        // ---------- lifecycle ----------

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            RefreshDrives(false);
            ApplyVisuals();
            DoLayout();

            RestorePosition();

            ApplyRegion();

            _sampler.Warmup();
            OnTick(null, null);
            _timer.Start();
            if (_settings.AlwaysOnTop) AssertTopmost();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ApplyRegion();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DPICHANGED)
            {
                RECT r = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                Location = new Point(r.left, r.top);
                DoLayout();
                ApplyRegion();
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
            if (m.Msg == WM_DISPLAYCHANGE)
            {
                // Resolution/layout changed — restore this layout's remembered position.
                RestorePosition();
                ApplyRegion();
            }
            else if (m.Msg == WM_SETTINGCHANGE)
            {
                // Sent with "ImmersiveColorSet" when the light/dark app theme is switched.
                string what = null;
                try { if (m.LParam != IntPtr.Zero) what = Marshal.PtrToStringAuto(m.LParam); }
                catch { }
                if (what == null || what.IndexOf("ImmersiveColorSet", StringComparison.OrdinalIgnoreCase) >= 0)
                    OnSystemThemeMaybeChanged();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _timer.Stop();
            SaveSettings();
            if (_helperEngaged) TempIpc.ClearHeartbeat(); // let the elevated helper exit
            if (_tray != null) _tray.Visible = false;
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_timer != null) _timer.Dispose();
                if (_tray != null) _tray.Dispose();
                if (_sampler != null) _sampler.Dispose();
                if (_procs != null) _procs.Dispose();
                if (_sysinfo != null) _sysinfo.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---------- drives ----------

        private void RefreshDrives(bool allowRelayout)
        {
            DriveLine[] arr = _sysinfo.Drives();

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (DriveLine dl in arr) { sb.Append(dl.Label); sb.Append((int)dl.TotalGB); sb.Append(';'); }
            string sig = sb.ToString();

            _drives.Drives = arr;
            _drives.Invalidate();

            if (sig != _driveSig)
            {
                _driveSig = sig;
                if (allowRelayout) DoLayout();
            }
        }

        // ---------- theme ----------

        private bool _themeIsDark = true;
        private bool _themeKnown;

        // Resolve the mode, and when the effective theme actually flips, repaint everything and move
        // any text colour the user never chose to the new theme's default (see Theme.Remap: white
        // clock hands on a white panel would otherwise be the first thing anyone reports).
        private void ApplyTheme()
        {
            Theme.Apply(_settings.Theme);
            if (_themeKnown && _themeIsDark == Theme.IsDark) return;
            _themeKnown = true;
            _themeIsDark = Theme.IsDark;

            // Also on the very first resolution, not just on a later flip: starting up in the light
            // theme with a saved white clock colour drew white on near-white, which is exactly the
            // case this exists for. Remap only ever touches a colour that still equals the other
            // theme's default, so a colour the user actually picked survives untouched.
            _settings.ClockColor = Theme.Remap(_settings.ClockColor, Theme.IsDark, Theme.DefaultClock);
            _settings.DateColor = Theme.Remap(_settings.DateColor, Theme.IsDark, Theme.DefaultDate);
            _settings.DayColor = Theme.Remap(_settings.DayColor, Theme.IsDark, Theme.DefaultDay);

            BackColor = Theme.WindowBack;
            foreach (Control c in Controls) { c.BackColor = Theme.PanelBack; c.Invalidate(); }
            Invalidate();
        }

        // Windows broadcasts this when the user switches between the light and dark app theme, which
        // is the whole point of "follow system" — without it the overlay would only notice on restart.
        private const int WM_SETTINGCHANGE = 0x001A;

        private void OnSystemThemeMaybeChanged()
        {
            if (_settings.Theme != ThemeMode.System) return;
            ApplyVisuals();
            DoLayout();
        }

        // ---------- temperature tiles ----------

        // Pick the sensors the user wants, in their order, and relayout only when the set changes —
        // sensors come and go (a drive is attached, the helper starts), and the section's height
        // depends on how many tiles there are.
        private readonly List<SensorReading> _tileBuf = new List<SensorReading>();
        private readonly System.Text.StringBuilder _tileSb = new System.Text.StringBuilder();
        private DateTime _lastTileUtc = DateTime.MinValue;

        private void RefreshTiles()
        {
            bool wantTemps = _settings.GetShow(Settings.SecTemps);
            bool wantFans = _settings.GetShow(Settings.SecFans);
            if (!wantTemps && !wantFans) return;

            // The sensors behind this only move every ~3 s, so there is nothing to gain from
            // rebuilding the lists on every tick — and the buffers are reused for the same reason.
            DateTime utc = DateTime.UtcNow;
            if ((utc - _lastTileUtc).TotalSeconds < 2) return;
            _lastTileUtc = utc;

            SensorReading[] all = _sampler.Sensors();
            _tileSb.Length = 0;
            if (wantTemps) Fill(_tempTiles, all, SensorKind.Temperature, _settings.TempTiles);
            if (wantFans) Fill(_fanTiles, all, SensorKind.Fan, _settings.FanTiles);

            string sig = _tileSb.ToString();
            if (sig != _tileSig)
            {
                _tileSig = sig;
                DoLayout();   // the section's height depends on how many tiles there are
            }
        }

        private void Fill(TilePanel panel, SensorReading[] all, SensorKind kind, List<string> chosen)
        {
            List<SensorReading> show = _tileBuf;
            show.Clear();
            if (chosen.Count == 0)
            {
                // No explicit choice yet: show everything of this kind that reads, which is what
                // makes the section work out of the box.
                for (int i = 0; i < all.Length; i++)
                    if (all[i].Kind == kind) show.Add(all[i]);
            }
            else
            {
                foreach (string id in chosen)
                    for (int i = 0; i < all.Length; i++)
                        if (all[i].Id == id && all[i].Kind == kind) { show.Add(all[i]); break; }
            }

            for (int i = 0; i < show.Count; i++) { _tileSb.Append(show[i].Id); _tileSb.Append(';'); }
            _tileSb.Append('|');
            panel.Items = show.ToArray();
            panel.Invalidate();
        }

        // ---------- tick ----------

        private void OnTick(object sender, EventArgs e)
        {
            // Accurate CPU temp (opt-in): keep the helper alive and feed its reading to the sampler.
            //
            // Everything here is throttled below the tick rate. Both the heartbeat write and the
            // temperature read are synchronous file I/O on the UI thread, and the helper only
            // publishes every 2 s against an 8 s staleness budget, so doing either every tick was
            // just putting the drawing thread on the disk three times as often as needed.
            if (_settings.AccurateCpuTempDriver)
            {
                DateTime utc = DateTime.UtcNow;
                if ((utc - _lastHeartbeatUtc).TotalSeconds >= 3)
                { _lastHeartbeatUtc = utc; TempIpc.WriteHeartbeat(); }

                // Back off while waiting for the one-time setup: Ready() ends in a late-bound COM
                // round-trip to Task Scheduler, and if the user declines the UAC prompt this would
                // otherwise repeat every 4 s for the rest of the session.
                double readyGap = _readyChecks < 5 ? 4.0 : (_readyChecks < 10 ? 30.0 : 60.0);
                if (!_pawnReady && (utc - _lastReadyCheckUtc).TotalSeconds >= readyGap)
                {
                    _pawnReady = PawnIoSetup.Ready();
                    _lastReadyCheckUtc = utc;
                    _readyChecks++;
                }
                if (_pawnReady && (utc - _lastTempReadUtc).TotalSeconds >= 2)
                {
                    _lastTempReadUtc = utc;

                    // Chipset + fan readings, if the wider probing is switched on. Only what actually
                    // arrived is merged in: a read that failed, or data old enough that the helper is
                    // plainly gone, says nothing about whether a given fan still exists, and the
                    // readings expire by themselves — so there is deliberately nothing to clear here.
                    if (_settings.WideSensors)
                    {
                        DateTime sw;
                        SensorReading[] extra = TempIpc.ReadSensors(out sw);
                        if (sw != DateTime.MinValue && (utc - sw).TotalSeconds < SensorMaxAgeSec)
                            _sampler.SetExtraSensors(extra);
                    }

                    double hc; DateTime hw;
                    if (TempIpc.TryReadCpuTemp(out hc, out hw) && (utc - hw).TotalSeconds < SensorMaxAgeSec)
                        _sampler.SetCpuTempOverride(hc);
                    else if ((utc - _lastTaskRunUtc).TotalSeconds >= 15)
                    {
                        _lastTaskRunUtc = utc;   // (re)start the helper via the task
                        PawnIoSetup.RunHelperTask();
                    }
                }
            }

            MetricsSnapshot s = _sampler.Sample();
            DateTime now = DateTime.Now;

            RefreshTiles();

            _clock.TimeText = now.ToString(_settings.ShowSeconds ? "HH:mm:ss" : "HH:mm", CultureInfo.CurrentCulture);
            _clock.Invalidate();

            _cpu.Available = s.CpuValid;
            _cpu.ValueText = s.CpuValid ? Pct(s.CpuPercent) : "n/a";
            if (s.CpuValid) _cpu.Add(s.CpuPercent); else _cpu.Invalidate();

            _gpu.Available = s.GpuValid;
            _gpu.ValueText = s.GpuValid ? Pct(s.GpuPercent) : "n/a";
            if (s.GpuValid) _gpu.Add(s.GpuPercent); else _gpu.Invalidate();

            _ram.Available = s.RamValid;
            if (s.RamValid)
            {
                _ram.ValueText = string.Format(CultureInfo.InvariantCulture, "{0:0.0}/{1:0.0} GB ({2:0}%)",
                    s.RamUsedBytes / GiB, s.RamTotalBytes / GiB, s.RamPercent);
                _ram.Add(s.RamPercent);
            }
            else { _ram.ValueText = "n/a"; _ram.Invalidate(); }

            _disk.Available = s.DiskValid;
            if (s.DiskValid)
            {
                _disk.ValueText = string.Format(CultureInfo.InvariantCulture, "{0:0}%  R {1} / W {2} MB/s",
                    s.DiskPercent, MBnum(s.DiskReadBps), MBnum(s.DiskWriteBps));
                _disk.Add(s.DiskPercent);
            }
            else { _disk.ValueText = "n/a"; _disk.Invalidate(); }

            _net.Available = s.NetValid;
            if (s.NetValid)
            {
                double down = ToUnit(s.NetDownBps);
                double up = ToUnit(s.NetUpBps);
                _net.ValueText = "↓ " + RateText(s.NetDownBps) + "  ↑ " + RateText(s.NetUpBps);
                _net.Add(down, up);
            }
            else { _net.ValueText = "n/a"; _net.Invalidate(); }

            // session totals (data volume, always in bytes-based units)
            double interval = _timer.Interval / 1000.0;
            _totalDownBytes += s.NetDownBps * interval;
            _totalUpBytes += s.NetUpBps * interval;
            _netTotals.DownText = "↓ " + Volume(_totalDownBytes);
            _netTotals.UpText = "↑ " + Volume(_totalUpBytes);
            _netTotals.Invalidate();

            _topCpu.Rows = _procs.TopCpu(); _topCpu.Invalidate();
            _topRam.Rows = _procs.TopRam(); _topRam.Invalidate();

            _ip.Lan = _sysinfo.InternalIp();
            _ip.Wan = _sysinfo.ExternalIp();
            _ip.Country = _sysinfo.WanCountry();
            _ip.Flag = _settings.ShowWanFlag ? FlagImage(_sysinfo.WanCc()) : null;
            _ip.Invalidate();

            RefreshDrives(true);

            _footer.DateText = now.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
            _footer.DayText = Capitalize(now.ToString("dddd", CultureInfo.CurrentCulture));
            _footer.Invalidate();

            AssertTopmost();
        }

        // bytes/sec -> graph value in the selected unit (MB/s or Mbps)
        private double ToUnit(double bytesPerSec)
        {
            return _settings.NetUnitBytes ? bytesPerSec / 1e6 : bytesPerSec * 8.0 / 1e6;
        }

        private string RateText(double bytesPerSec)
        {
            if (_settings.NetUnitBytes)
            {
                double b = bytesPerSec;
                if (b >= 1e6) return string.Format(CultureInfo.InvariantCulture, "{0:0.0} MB/s", b / 1e6);
                if (b >= 1e3) return string.Format(CultureInfo.InvariantCulture, "{0:0} kB/s", b / 1e3);
                return string.Format(CultureInfo.InvariantCulture, "{0:0} B/s", b);
            }
            double bits = bytesPerSec * 8.0;
            if (bits >= 1e6) return string.Format(CultureInfo.InvariantCulture, "{0:0.0} Mbps", bits / 1e6);
            if (bits >= 1e3) return string.Format(CultureInfo.InvariantCulture, "{0:0} Kbps", bits / 1e3);
            return string.Format(CultureInfo.InvariantCulture, "{0:0} bps", bits);
        }

        private static string Volume(double bytes)
        {
            if (bytes >= GiB) return string.Format(CultureInfo.InvariantCulture, "{0:0.00} GB", bytes / GiB);
            if (bytes >= 1024.0 * 1024) return string.Format(CultureInfo.InvariantCulture, "{0:0.0} MB", bytes / (1024.0 * 1024));
            return string.Format(CultureInfo.InvariantCulture, "{0:0} KB", bytes / 1024.0);
        }

        private static string Pct(double v) { return string.Format(CultureInfo.InvariantCulture, "{0:0}%", v); }

        private static string MBnum(double bytesPerSec)
        {
            double mb = bytesPerSec / 1e6;
            return mb >= 100 ? mb.ToString("0", CultureInfo.InvariantCulture) : mb.ToString("0.0", CultureInfo.InvariantCulture);
        }

        private static string Capitalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return char.ToUpper(s[0]) + s.Substring(1);
        }

        private void AssertTopmost()
        {
            if (!_settings.AlwaysOnTop) return;
            if (IsHandleCreated)
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        private void SaveSettings()
        {
            _settings.SetPos(CurrentSig(), Location.X, Location.Y); // per-resolution memory
            _settings.HasPosition = true;                            // legacy generic fallback
            _settings.X = Location.X;
            _settings.Y = Location.Y;
            _settings.Opacity = Opacity;
            _settings.Save();
        }

        private static double ClampOpacity(double o)
        {
            if (o < 0.3) return 0.3;
            if (o > 1.0) return 1.0;
            return o;
        }
    }
}
