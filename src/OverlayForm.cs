using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LoadView
{
    // The always-on-top, draggable, semi-transparent overlay. Top to bottom: clock, five
    // metric graphs (CPU/GPU/MEM/DISK/NET), a text list of drives, then date + weekday.
    internal sealed class OverlayForm : Form
    {
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
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
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private const double GiB = 1024.0 * 1024.0 * 1024.0;

        private readonly MetricsSampler _sampler;
        private readonly Timer _timer;
        private readonly Settings _settings;

        private ClockPanel _clock;
        private GraphPanel _cpu, _gpu, _ram, _disk, _net;
        private DrivesPanel _drives;
        private FooterPanel _footer;

        private ContextMenuStrip _menu;
        private NotifyIcon _tray;

        private bool _dragging;
        private Point _dragMouseStart;
        private Point _dragFormStart;

        private int _driveTick;
        private string _driveSig = "";

        public OverlayForm()
        {
            _settings = Settings.Load();
            _sampler = new MetricsSampler();

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(12, 12, 14); // shows through 1px gaps as separators
            Font = new Font("Segoe UI", 9f);
            DoubleBuffered = true;
            Opacity = ClampOpacity(_settings.Opacity);
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
            // Tool window => no taskbar button and no Alt+Tab entry.
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= WS_EX_TOOLWINDOW; return cp; }
        }

        // ---------- panels ----------

        private void BuildPanels()
        {
            _clock = new ClockPanel();

            _cpu  = NewGraph("CPU",  false, Color.FromArgb(0x4F, 0x8C, 0xFF));
            _gpu  = NewGraph("GPU",  false, Color.FromArgb(0x36, 0xC7, 0x9B));
            _ram  = NewGraph("MEM",  false, Color.FromArgb(0xB0, 0x7C, 0xFF));
            _disk = NewGraph("DISK", false, Color.FromArgb(0x6F, 0xD0, 0x57));
            _net  = NewGraph("NET",  true,  Color.FromArgb(0xFF, 0x9F, 0x40));
            _net.Accent2 = Color.FromArgb(0x55, 0xD6, 0xFF);
            _net.Percent = false;
            _net.MinScale = 1; // 1 Mbps floor
            _net.ScaleLabeler = FormatMbpsScale;

            _drives = new DrivesPanel();
            _footer = new FooterPanel();

            Control[] all = new Control[] { _clock, _cpu, _gpu, _ram, _disk, _net, _drives, _footer };
            foreach (Control c in all)
            {
                c.ContextMenuStrip = SharedMenu();
                WireDrag(c);
                Controls.Add(c);
            }
        }

        private static GraphPanel NewGraph(string title, bool two, Color accent)
        {
            GraphPanel p = new GraphPanel(two);
            p.Title = title;
            p.Accent = accent;
            return p;
        }

        // ---------- menus / tray ----------

        private ContextMenuStrip SharedMenu()
        {
            if (_menu != null) return _menu;
            _menu = new ContextMenuStrip();
            _menu.Items.Add("Reset position", null, delegate { ResetPosition(); });

            ToolStripMenuItem op = new ToolStripMenuItem("Opacity");
            op.DropDownItems.Add("50%", null, delegate { SetOpacity(0.50); });
            op.DropDownItems.Add("70%", null, delegate { SetOpacity(0.70); });
            op.DropDownItems.Add("85%", null, delegate { SetOpacity(0.85); });
            op.DropDownItems.Add("100%", null, delegate { SetOpacity(1.00); });
            _menu.Items.Add(op);

            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("Exit", null, delegate { Close(); });
            return _menu;
        }

        private void BuildTray()
        {
            _tray = new NotifyIcon();
            _tray.Text = "LoadView";
            _tray.Icon = MakeIcon();
            _tray.Visible = true;
            _tray.DoubleClick += delegate { ToggleVisible(); };

            ContextMenuStrip tm = new ContextMenuStrip();
            tm.Items.Add("Show / Hide", null, delegate { ToggleVisible(); });
            tm.Items.Add("Reset position", null, delegate { ResetPosition(); });
            tm.Items.Add(new ToolStripSeparator());
            tm.Items.Add("Exit", null, delegate { Close(); });
            _tray.ContextMenuStrip = tm;
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

        private void ToggleVisible()
        {
            Visible = !Visible;
            if (Visible) { TopMost = true; AssertTopmost(); }
        }

        private void ResetPosition()
        {
            DoLayout();
            Location = DefaultLocation(Size);
            ApplyRegion();
            SaveSettings();
        }

        private void SetOpacity(double o)
        {
            Opacity = ClampOpacity(o);
            SaveSettings();
        }

        // ---------- sizing / layout ----------

        private float Scale() { return DeviceDpi / 96f; }

        private void DoLayout()
        {
            float s = Scale();
            int w = (int)(300 * s);
            int gap = Math.Max(1, (int)(1 * s));
            int clockH = (int)(42 * s);
            int graphH = (int)(58 * s);
            int footerH = (int)(48 * s);
            int rowPx = (int)(18 * s);
            int driveRows = (_drives.Drives != null ? _drives.Drives.Length : 0) + 1; // + header
            int drivesH = driveRows * rowPx + (int)(8 * s);

            int y = 0;
            PlaceRow(_clock, w, ref y, clockH, gap);
            PlaceRow(_cpu, w, ref y, graphH, gap);
            PlaceRow(_gpu, w, ref y, graphH, gap);
            PlaceRow(_ram, w, ref y, graphH, gap);
            PlaceRow(_disk, w, ref y, graphH, gap);
            PlaceRow(_net, w, ref y, graphH, gap);
            PlaceRow(_drives, w, ref y, drivesH, gap);
            PlaceRow(_footer, w, ref y, footerH, 0);

            ClientSize = new Size(w, y);
        }

        private static void PlaceRow(Control c, int w, ref int y, int h, int gap)
        {
            c.SetBounds(0, y, w, h);
            y += h + gap;
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

        private void ApplyRegion()
        {
            if (!IsHandleCreated) return;
            int radius = (int)(10 * Scale());
            IntPtr rgn = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, radius, radius);
            Region = Region.FromHrgn(rgn);
            DeleteObject(rgn);
        }

        // ---------- drives ----------

        private void RefreshDrives(bool allowRelayout)
        {
            List<DriveLine> list = new List<DriveLine>();
            try
            {
                foreach (DriveInfo di in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (!di.IsReady) continue;
                        if (di.DriveType != DriveType.Fixed && di.DriveType != DriveType.Removable) continue;
                        double total = di.TotalSize / GiB;
                        if (total <= 0) continue;
                        double free = di.TotalFreeSpace / GiB;
                        double used = total - free;

                        DriveLine dl;
                        dl.Label = di.Name.TrimEnd('\\');
                        dl.UsedGB = used;
                        dl.TotalGB = total;
                        dl.Pct = total > 0 ? 100.0 * used / total : 0;
                        list.Add(dl);
                    }
                    catch { }
                }
            }
            catch { }

            DriveLine[] arr = list.ToArray();

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

        // ---------- lifecycle ----------

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            RefreshDrives(false);
            DoLayout();

            Rectangle saved = new Rectangle(new Point(_settings.X, _settings.Y), Size);
            Location = (_settings.HasPosition && IsOnScreen(saved))
                ? new Point(_settings.X, _settings.Y)
                : DefaultLocation(Size);

            ApplyRegion();

            _sampler.Warmup();
            OnTick(null, null); // populate immediately
            _timer.Start();
            AssertTopmost();
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
            }
            base.Dispose(disposing);
        }

        // ---------- tick / formatting ----------

        private void OnTick(object sender, EventArgs e)
        {
            MetricsSnapshot s = _sampler.Sample();
            DateTime now = DateTime.Now;

            _clock.TimeText = now.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
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
                _net.ValueText = "↓ " + Rate(s.NetDownBps) + "  ↑ " + Rate(s.NetUpBps);
                _net.Add(s.NetDownBps * 8.0 / 1e6, s.NetUpBps * 8.0 / 1e6); // Mbps
            }
            else { _net.ValueText = "n/a"; _net.Invalidate(); }

            _footer.DateText = now.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
            _footer.DayText = Capitalize(now.ToString("dddd", CultureInfo.CurrentCulture));
            _footer.Invalidate();

            if (_driveTick % 5 == 0) RefreshDrives(true);
            _driveTick++;

            AssertTopmost();
        }

        private static string Pct(double v) { return string.Format(CultureInfo.InvariantCulture, "{0:0}%", v); }

        private static string Temp(double c) { return string.Format(CultureInfo.InvariantCulture, "{0:0}°C", c); }

        private static string MBnum(double bytesPerSec)
        {
            double mb = bytesPerSec / 1e6;
            return mb >= 100
                ? mb.ToString("0", CultureInfo.InvariantCulture)
                : mb.ToString("0.0", CultureInfo.InvariantCulture);
        }

        private static string Rate(double bytesPerSec)
        {
            double bits = bytesPerSec * 8.0;
            if (bits >= 1e6) return string.Format(CultureInfo.InvariantCulture, "{0:0.0} Mbps", bits / 1e6);
            if (bits >= 1e3) return string.Format(CultureInfo.InvariantCulture, "{0:0} Kbps", bits / 1e3);
            return string.Format(CultureInfo.InvariantCulture, "{0:0} bps", bits);
        }

        // Label for the network graph's auto-scaled ceiling (value is in Mbps).
        private static string FormatMbpsScale(double mbps)
        {
            if (mbps >= 1000) return string.Format(CultureInfo.InvariantCulture, "{0:0} Gbps", mbps / 1000.0);
            if (mbps >= 1) return string.Format(CultureInfo.InvariantCulture, "{0:0} Mbps", mbps);
            return string.Format(CultureInfo.InvariantCulture, "{0:0} Kbps", mbps * 1000.0);
        }

        private static string Capitalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return char.ToUpper(s[0]) + s.Substring(1);
        }

        private void AssertTopmost()
        {
            if (IsHandleCreated)
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        private void SaveSettings()
        {
            _settings.HasPosition = true;
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
