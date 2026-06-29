using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace LoadView
{
    internal struct DriveLine
    {
        public string Label;
        public double UsedGB;
        public double TotalGB;
        public double Pct;
    }

    internal abstract class InfoPanelBase : Control
    {
        protected static readonly Color PanelBack = Color.FromArgb(26, 26, 30);
        protected static readonly Color DimColor = Color.FromArgb(150, 150, 158);

        protected InfoPanelBase()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = PanelBack;
        }

        // Device-pixel vertical padding that scales with DPI.
        protected int Pad() { return (int)(8 * DeviceDpi / 96f); }
    }

    // Big centered clock (HH:mm:ss or HH:mm). Size + color configurable.
    internal sealed class ClockPanel : InfoPanelBase
    {
        public string TimeText = "";
        public float SizePt = 20f;
        public Color Ink = Color.FromArgb(232, 232, 237);

        private Font _font;
        private float _builtSize;

        private Font F()
        {
            if (_font == null || _builtSize != SizePt)
            {
                if (_font != null) _font.Dispose();
                _font = new Font(Font.FontFamily, SizePt, FontStyle.Bold);
                _builtSize = SizePt;
            }
            return _font;
        }

        public int PreferredHeight()
        {
            return (int)Math.Ceiling(F().GetHeight(DeviceDpi)) + Pad();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            TextRenderer.DrawText(g, TimeText, F(), ClientRectangle, Ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _font != null) _font.Dispose();
            base.Dispose(disposing);
        }
    }

    // Date (DD.MM.YYYY) on top, weekday below. Sizes + colors configurable.
    internal sealed class FooterPanel : InfoPanelBase
    {
        public string DateText = "";
        public string DayText = "";
        public float DateSizePt = 13f;
        public float DaySizePt = 10f;
        public Color DateInk = Color.FromArgb(232, 232, 237);
        public Color DayInk = Color.FromArgb(150, 150, 158);

        private Font _date, _day;
        private float _builtDate, _builtDay;

        private Font DateFont()
        {
            if (_date == null || _builtDate != DateSizePt)
            {
                if (_date != null) _date.Dispose();
                _date = new Font(Font.FontFamily, DateSizePt, FontStyle.Bold);
                _builtDate = DateSizePt;
            }
            return _date;
        }

        private Font DayFont()
        {
            if (_day == null || _builtDay != DaySizePt)
            {
                if (_day != null) _day.Dispose();
                _day = new Font(Font.FontFamily, DaySizePt, FontStyle.Regular);
                _builtDay = DaySizePt;
            }
            return _day;
        }

        public int PreferredHeight()
        {
            return (int)Math.Ceiling(DateFont().GetHeight(DeviceDpi))
                 + (int)Math.Ceiling(DayFont().GetHeight(DeviceDpi))
                 + Pad();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            int dateH = (int)Math.Ceiling(DateFont().GetHeight(DeviceDpi));
            int pad = Pad() / 2;
            Rectangle top = new Rectangle(0, pad, Width, dateH);
            Rectangle bot = new Rectangle(0, top.Bottom, Width, Height - top.Bottom);
            TextRenderer.DrawText(g, DateText, DateFont(), top, DateInk,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, DayText, DayFont(), bot, DayInk,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPadding);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_date != null) _date.Dispose();
                if (_day != null) _day.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // Drives as "This PC"-style rows: a label + a horizontal usage bar.
    // The form sets HeaderPx / DriveRowPx (device pixels) before layout.
    internal sealed class DrivesPanel : InfoPanelBase
    {
        public DriveLine[] Drives = new DriveLine[0];
        public int HeaderPx = 18;
        public int DriveRowPx = 30;
        public Color Accent = Color.FromArgb(0x4F, 0x8C, 0xFF);

        private static readonly Color TextColor = Color.FromArgb(232, 232, 237);
        private static readonly Color Track = Color.FromArgb(52, 52, 60);
        private static readonly Color NearFull = Color.FromArgb(0xE0, 0x4F, 0x4F);

        private Font _hdr;

        private Font HeaderFont()
        {
            if (_hdr == null) _hdr = new Font(Font, FontStyle.Bold);
            return _hdr;
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (_hdr != null) { _hdr.Dispose(); _hdr = null; }
        }

        public int ContentHeight(int driveCount)
        {
            return HeaderPx + driveCount * DriveRowPx;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            DriveLine[] d = Drives == null ? new DriveLine[0] : Drives;
            int pad = Math.Max(4, Width / 24);
            int contentW = Width - 2 * pad;

            Rectangle hr = new Rectangle(pad, 0, contentW, HeaderPx);
            TextRenderer.DrawText(g, "DRIVES", HeaderFont(), hr, DimColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            int gap = Math.Max(2, DriveRowPx / 12);
            for (int i = 0; i < d.Length; i++)
            {
                int rowY = HeaderPx + i * DriveRowPx;
                int labelH = (int)(DriveRowPx * 0.52);
                int barH = Math.Max(4, (int)(DriveRowPx * 0.28));

                Rectangle lr = new Rectangle(pad, rowY, contentW, labelH);
                TextRenderer.DrawText(g, FormatDrive(d[i]), Font, lr, TextColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                int barY = rowY + labelH + gap;
                Rectangle track = new Rectangle(pad, barY, contentW, barH);
                using (SolidBrush tb = new SolidBrush(Track))
                    g.FillRectangle(tb, track);

                double pct = d[i].Pct; if (pct < 0) pct = 0; else if (pct > 100) pct = 100;
                int fillW = (int)(contentW * pct / 100.0);
                if (fillW > 0)
                {
                    Color fc = pct >= 90 ? NearFull : Accent;
                    using (SolidBrush fb = new SolidBrush(fc))
                        g.FillRectangle(fb, new Rectangle(pad, barY, fillW, barH));
                }
                using (Pen bp = new Pen(Color.FromArgb(70, 70, 78)))
                    g.DrawRectangle(bp, track.X, track.Y, track.Width - 1, track.Height - 1);
            }
        }

        private static string FormatDrive(DriveLine d)
        {
            string cap;
            if (d.TotalGB >= 1024.0)
                cap = string.Format(CultureInfo.InvariantCulture, "{0:0.0} / {1:0.0} TB", d.UsedGB / 1024.0, d.TotalGB / 1024.0);
            else
                cap = string.Format(CultureInfo.InvariantCulture, "{0:0} / {1:0} GB", d.UsedGB, d.TotalGB);
            return string.Format(CultureInfo.InvariantCulture, "{0}  {1}  ({2:0}%)", d.Label, cap, d.Pct);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _hdr != null) _hdr.Dispose();
            base.Dispose(disposing);
        }
    }
}
