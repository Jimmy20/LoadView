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
        private IpPanel _ip;
        private FooterPanel _footer;

        private ContextMenuStrip _menu;
        private ToolStripMenuItem _lockItem;
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

            Control[] all = new Control[]
            {
                _clock, _cpu, _gpu, _ram, _disk, _net,
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
            _menu.Items.Add("Reset position", null, delegate { ResetPosition(); });
            _menu.Items.Add("Settings...", null, delegate { OpenSettings(); });
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
            tm.Items.Add("Reset position", null, delegate { ResetPosition(); });
            tm.Items.Add("Settings...", null, delegate { OpenSettings(); });
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
            using (SettingsForm f = new SettingsForm(_settings.Clone(), ApplySettings))
                f.ShowDialog(this);
        }

        private void OpenAbout()
        {
            using (AboutForm a = new AboutForm())
                a.ShowDialog(this);
        }

        private void ToggleLock()
        {
            _settings.Locked = !_settings.Locked;
            if (_lockItem != null) _lockItem.Checked = _settings.Locked;
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

        // ---------- apply settings ----------

        private void ApplySettings(Settings s)
        {
            _settings = s;
            ApplyVisuals();
            DoLayout();
            if (_settings.AlwaysOnTop) AssertTopmost();
            SaveSettings();
        }

        private void ApplyVisuals()
        {
            Opacity = ClampOpacity(_settings.Opacity);
            TopMost = _settings.AlwaysOnTop;
            Log.Enabled = _settings.DebugLog;

            int ms = _settings.RefreshMs;
            if (ms < 200) ms = 200; else if (ms > 10000) ms = 10000;
            if (_timer != null) _timer.Interval = ms;

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

            _ip.ShowWan = _settings.ExternalIpEnabled;
            _sysinfo.ExternalIpEnabled = _settings.ExternalIpEnabled;
            _sysinfo.LanRefreshSec = _settings.IpLanRefreshSec;
            _sysinfo.WanRefreshSec = _settings.IpWanRefreshSec;

            foreach (string key in Settings.AllSections)
            {
                Control c = PanelFor(key);
                if (c != null) c.Visible = _settings.GetShow(key);
            }

            if (_lockItem != null) _lockItem.Checked = _settings.Locked;
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
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _timer.Stop();
            SaveSettings();
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

        // ---------- tick ----------

        private void OnTick(object sender, EventArgs e)
        {
            MetricsSnapshot s = _sampler.Sample();
            DateTime now = DateTime.Now;

            _clock.TimeText = now.ToString(_settings.ShowSeconds ? "HH:mm:ss" : "HH:mm", CultureInfo.CurrentCulture);
            _clock.Invalidate();

            string cpuTemp = s.CpuTempValid ? " · " + Temp(s.CpuTempC) : "";
            _cpu.Available = s.CpuValid;
            _cpu.ValueText = (s.CpuValid ? Pct(s.CpuPercent) : "n/a") + cpuTemp;
            if (s.CpuValid) _cpu.Add(s.CpuPercent); else _cpu.Invalidate();

            string gpuTemp = s.GpuTempValid ? " · " + Temp(s.GpuTempC) : "";
            _gpu.Available = s.GpuValid;
            _gpu.ValueText = (s.GpuValid ? Pct(s.GpuPercent) : "n/a") + gpuTemp;
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
        private static string Temp(double c) { return string.Format(CultureInfo.InvariantCulture, "{0:0}°C", c); }

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
