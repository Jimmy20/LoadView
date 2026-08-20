using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using LoadView;

// Product shot for the store listings: the real overlay panels, laid out in the real order, with
// illustrative readings. Rendered offscreen rather than captured from the screen, so nothing from
// the desktop (or a real IP) ends up in a picture that lives on the internet.
//
// Not part of the app - build.ps1 only compiles src\*.cs. Run it from the repo root:
//
//   & "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /main:Shot ^
//       /platform:x64 /codepage:65001 /out:shot.exe /r:System.dll /r:System.Drawing.dll ^
//       /r:System.Windows.Forms.dll /r:System.Management.dll /r:System.IO.Compression.dll ^
//       /r:System.IO.Compression.FileSystem.dll tools\shot.cs src\*.cs
//   .\shot.exe docs        # writes shot-dark.png, shot-light.png, icon-256.png
//
// Then tools\make-social.ps1 rebuilds the link card from docs\Screenshot.png.
internal static class Shot
{
    private const int W = 300;

    [STAThread]
    private static void Main(string[] a)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string dir = a.Length > 0 ? a[0] : ".";

        Render(dir + "\\shot-dark.png", ThemeMode.Dark);
        Render(dir + "\\shot-light.png", ThemeMode.Light);
        Console.WriteLine("done");
    }

    private static void Render(string path, ThemeMode mode)
    {
        Theme.Apply(mode);
        using (Form f = new Form())
        {
            f.FormBorderStyle = FormBorderStyle.None;
            f.ShowInTaskbar = false;
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-4000, -4000);   // offscreen while its handle exists
            f.Font = new Font("Segoe UI", 9f);
            f.BackColor = Theme.WindowBack;
            f.ClientSize = new Size(W, 1400);

            List<Control> stack = new List<Control>();
            stack.Add(Clock());
            stack.Add(Temps());
            stack.Add(Fans());
            stack.Add(Graph("CPU", "34%", Color.FromArgb(0x4F, 0x8C, 0xFF), Cpu()));
            stack.Add(Graph("GPU", "18%", Color.FromArgb(0x36, 0xC7, 0x9B), Gpu()));
            stack.Add(Graph("MEM", "21.4/31.7 GB (67%)", Color.FromArgb(0xB0, 0x7C, 0xFF), Mem()));
            stack.Add(Graph("DISK", "6%  R 1.2 / W 0.4 MB/s", Color.FromArgb(0x6F, 0xD0, 0x57), Disk()));
            stack.Add(Net());
            stack.Add(Totals());
            stack.Add(MakeList("TOP CPU", false, new string[] { "chrome", "Code", "explorer", "svchost", "LoadView" },
                new double[] { 9, 6, 3, 2, 1 }));
            stack.Add(MakeList("TOP RAM", true, new string[] { "chrome", "Code", "firefox", "explorer", "svchost" },
                new double[] { 3.7e9, 2.8e9, 1.6e9, 1.3e9, 0.9e9 }));
            stack.Add(Drives());
            stack.Add(Ip());
            stack.Add(Footer());

            int y = 0;
            foreach (Control c in stack)
            {
                c.BackColor = Theme.PanelBack;
                c.SetBounds(0, y, W, c.Height);
                f.Controls.Add(c);
                y += c.Height;
            }
            f.ClientSize = new Size(W, y);
            f.Show();

            using (Bitmap bmp = new Bitmap(W, y))
            {
                f.DrawToBitmap(bmp, new Rectangle(0, 0, W, y));
                bmp.Save(path, ImageFormat.Png);
            }
            Console.WriteLine(System.IO.Path.GetFileName(path) + ": " + W + "x" + y);
            f.Hide();
        }
    }

    // ---- panels, styled with the app's own defaults ----

    private static Control Clock()
    {
        ClockPanel p = new ClockPanel();
        p.TimeText = "10:24";
        p.SizePt = 50f;
        p.Ink = Theme.DefaultClock(Theme.IsDark);
        p.Height = 92;
        return p;
    }

    private static Control Temps()
    {
        TilePanel p = new TilePanel();
        p.Header = "TEMPERATURES";
        p.TilePx = 46;
        p.HotCpuC = 90; p.HotGpuC = 85; p.HotDiskC = 55; p.HotOtherC = 80;
        p.Items = new SensorReading[]
        {
            S("cpu", "CPU", SensorKind.Temperature, 61),
            S("gpu", "GPU", SensorKind.Temperature, 47),
            S("disk:a", "C:", SensorKind.Temperature, 44),
            S("disk:b", "D:", SensorKind.Temperature, 39),
            S("disk:c", "E:", SensorKind.Temperature, 34),
            S("disk:d", "G:", SensorKind.Temperature, 37)
        };
        p.Width = W;
        p.Height = p.ContentHeight();
        return p;
    }

    private static Control Fans()
    {
        TilePanel p = new TilePanel();
        p.Header = "FANS (RPM)";
        p.TilePx = 46;
        p.Items = new SensorReading[]
        {
            S("/lpc/f/1", "CPU", SensorKind.Fan, 1180),
            S("/lpc/f/2", "Sys 1", SensorKind.Fan, 902),
            S("/lpc/f/3", "Sys 2", SensorKind.Fan, 874),
            S("/lpc/f/4", "GPU", SensorKind.Fan, 1420)
        };
        p.Width = W;
        p.Height = p.ContentHeight();
        return p;
    }

    private static Control Graph(string title, string value, Color accent, double[] data)
    {
        GraphPanel g = new GraphPanel(false);
        g.Title = title;
        g.ValueText = value;
        g.Accent = accent;
        g.Percent = true;
        g.Height = 120;
        g.Width = W;
        for (int i = 0; i < data.Length; i++) g.Add(data[i]);
        return g;
    }

    private static Control Net()
    {
        GraphPanel g = new GraphPanel(true);
        g.Title = "NET";
        g.ValueText = "\u2193 1.8 MB/s  \u2191 0.2 MB/s";
        g.Accent = Color.FromArgb(0x57, 0xD0, 0x6F);
        g.Accent2 = Color.FromArgb(0xE0, 0x5A, 0x5A);
        g.Percent = false;
        g.Height = 120;
        g.Width = W;
        double[] d = NetData();
        for (int i = 0; i < d.Length; i++) g.Add(d[i], d[i] * 0.16);
        return g;
    }

    private static Control Totals()
    {
        NetTotalsPanel p = new NetTotalsPanel();
        p.DownText = "3.4 GB";
        p.UpText = "412 MB";
        p.TextSize = 12f;
        p.Width = W;
        p.Height = p.PreferredHeight();
        return p;
    }

    private static Control MakeList(string header, bool bytes, string[] names, double[] values)
    {
        ListPanel p = new ListPanel();
        p.Header = header;
        p.IsBytes = bytes;
        p.TextSize = 11f;
        ProcEntry[] rows = new ProcEntry[names.Length];
        for (int i = 0; i < names.Length; i++) { rows[i].Name = names[i]; rows[i].Value = values[i]; }
        p.Rows = rows;
        p.Width = W;
        p.Height = p.PreferredHeight();
        return p;
    }

    private static Control Drives()
    {
        DrivesPanel p = new DrivesPanel();
        p.DriveRowPx = 50;
        p.LabelSize = 9f;
        p.Drives = new DriveLine[] { D("C:", 412, 931), D("D:", 690, 1863) };
        p.Width = W;
        p.Height = p.ContentHeight(p.Drives.Length) + 8;
        return p;
    }

    private static Control Ip()
    {
        IpPanel p = new IpPanel();
        p.Lan = "192.168.1.23";
        p.Wan = "203.0.113.42";
        p.ShowWan = true;
        p.TextSize = 11f;
        p.Width = W;
        p.Height = p.PreferredHeight();
        return p;
    }

    private static Control Footer()
    {
        FooterPanel p = new FooterPanel();
        p.DateText = "20.08.2026";
        p.DayText = "Thursday";
        p.DateSizePt = 20f;
        p.DaySizePt = 25f;
        p.DateBold = true;
        p.DayBold = true;
        p.DateInk = Theme.DefaultDate(Theme.IsDark);
        p.DayInk = Theme.DefaultDay(Theme.IsDark);
        p.Width = W;
        p.Height = p.PreferredHeight();
        return p;
    }

    // ---- data that looks like a working machine, not a sine wave ----

    private static double[] Cpu()
    {
        double[] d = new double[60];
        Random r = new Random(7);
        double v = 22;
        for (int i = 0; i < d.Length; i++)
        {
            v += r.Next(-9, 11);
            if (i == 34) v = 71;
            if (i == 35) v = 64;
            if (v < 6) v = 6;
            if (v > 88) v = 88;
            d[i] = v;
        }
        return d;
    }

    private static double[] Gpu()
    {
        double[] d = new double[60];
        Random r = new Random(11);
        for (int i = 0; i < d.Length; i++) d[i] = 6 + r.Next(0, 14) + (i > 40 ? 12 : 0);
        return d;
    }

    private static double[] Mem()
    {
        double[] d = new double[60];
        for (int i = 0; i < d.Length; i++) d[i] = 63 + (i / 12.0) + (i % 7 == 0 ? 1 : 0);
        return d;
    }

    private static double[] Disk()
    {
        double[] d = new double[60];
        Random r = new Random(3);
        for (int i = 0; i < d.Length; i++) d[i] = r.Next(0, 9);
        d[18] = 46; d[19] = 61; d[20] = 28; d[44] = 37;
        return d;
    }

    private static double[] NetData()
    {
        double[] d = new double[60];
        Random r = new Random(5);
        for (int i = 0; i < d.Length; i++) d[i] = 0.1 + r.NextDouble() * 0.5;
        for (int i = 26; i < 34; i++) d[i] = 1.4 + r.NextDouble() * 0.7;
        return d;
    }

    private static SensorReading S(string id, string label, SensorKind k, double v)
    {
        SensorReading s = new SensorReading();
        s.Id = id; s.Label = label; s.Kind = k; s.Value = v;
        return s;
    }

    private static DriveLine D(string label, double used, double total)
    {
        DriveLine d = new DriveLine();
        d.Label = label; d.UsedGB = used; d.TotalGB = total;
        d.FreeGB = total - used; d.Pct = used / total * 100.0;
        return d;
    }
}
