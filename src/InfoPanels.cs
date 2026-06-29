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
        public double FreeGB;
        public double Pct;
    }

    internal abstract class InfoPanelBase : Control
    {
        protected static readonly Color PanelBack = Color.FromArgb(26, 26, 30);
        protected static readonly Color TextColor = Color.FromArgb(232, 232, 237);
        protected static readonly Color DimColor = Color.FromArgb(150, 150, 158);

        protected InfoPanelBase()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = PanelBack;
        }

        protected int Pad() { return (int)(8 * DeviceDpi / 96f); }
        protected int LineH(Font f) { return (int)Math.Ceiling(f.GetHeight(DeviceDpi)); }

        protected static string CapShort(double gb)
        {
            return gb >= 1024.0
                ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} TB", gb / 1024.0)
                : string.Format(CultureInfo.InvariantCulture, "{0:0} GB", gb);
        }

        protected static string Bytes(double b)
        {
            if (b >= 1024.0 * 1024 * 1024) return string.Format(CultureInfo.InvariantCulture, "{0:0.0} GB", b / (1024.0 * 1024 * 1024));
            if (b >= 1024.0 * 1024) return string.Format(CultureInfo.InvariantCulture, "{0:0} MB", b / (1024.0 * 1024));
            return string.Format(CultureInfo.InvariantCulture, "{0:0} KB", b / 1024.0);
        }
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

        public int PreferredHeight() { return LineH(F()) + Pad(); }

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

    // Date on top, weekday below — both centered, size/color/bold configurable.
    internal sealed class FooterPanel : InfoPanelBase
    {
        public string DateText = "";
        public string DayText = "";
        public float DateSizePt = 13f;
        public float DaySizePt = 10f;
        public bool DateBold = true;
        public bool DayBold = false;
        public Color DateInk = Color.FromArgb(232, 232, 237);
        public Color DayInk = Color.FromArgb(150, 150, 158);

        private Font _date, _day;
        private float _bDateSize, _bDaySize;
        private bool _bDateBold, _bDayBold;

        private Font DateFont()
        {
            if (_date == null || _bDateSize != DateSizePt || _bDateBold != DateBold)
            {
                if (_date != null) _date.Dispose();
                _date = new Font(Font.FontFamily, DateSizePt, DateBold ? FontStyle.Bold : FontStyle.Regular);
                _bDateSize = DateSizePt; _bDateBold = DateBold;
            }
            return _date;
        }

        private Font DayFont()
        {
            if (_day == null || _bDaySize != DaySizePt || _bDayBold != DayBold)
            {
                if (_day != null) _day.Dispose();
                _day = new Font(Font.FontFamily, DaySizePt, DayBold ? FontStyle.Bold : FontStyle.Regular);
                _bDaySize = DaySizePt; _bDayBold = DayBold;
            }
            return _day;
        }

        public int PreferredHeight() { return LineH(DateFont()) + LineH(DayFont()) + Pad(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            int dateH = LineH(DateFont());
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
            if (disposing) { if (_date != null) _date.Dispose(); if (_day != null) _day.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // Drives as "This PC"-style rows: label (left) + free space (right) + usage bar.
    internal sealed class DrivesPanel : InfoPanelBase
    {
        public DriveLine[] Drives = new DriveLine[0];
        public int HeaderPx = 18;
        public int DriveRowPx = 30;
        public Color Accent = Color.FromArgb(0x4F, 0x8C, 0xFF);
        public float LabelSize = 9f;
        public bool LabelBold = false;

        private static readonly Color Track = Color.FromArgb(52, 52, 60);
        private static readonly Color NearFull = Color.FromArgb(0xE0, 0x4F, 0x4F);

        private Font _hdr, _lbl;
        private float _bLblSize;
        private bool _bLblBold;

        private Font HeaderFont()
        {
            if (_hdr == null) _hdr = new Font(Font, FontStyle.Bold);
            return _hdr;
        }

        private Font LabelFont()
        {
            if (_lbl == null || _bLblSize != LabelSize || _bLblBold != LabelBold)
            {
                if (_lbl != null) _lbl.Dispose();
                _lbl = new Font(Font.FontFamily, LabelSize, LabelBold ? FontStyle.Bold : FontStyle.Regular);
                _bLblSize = LabelSize; _bLblBold = LabelBold;
            }
            return _lbl;
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (_hdr != null) { _hdr.Dispose(); _hdr = null; }
        }

        public int ContentHeight(int driveCount) { return HeaderPx + driveCount * DriveRowPx; }

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

            Font lf = LabelFont();
            int gap = Math.Max(2, DriveRowPx / 12);
            for (int i = 0; i < d.Length; i++)
            {
                int rowY = HeaderPx + i * DriveRowPx;
                int labelH = (int)(DriveRowPx * 0.52);
                int barH = Math.Max(4, (int)(DriveRowPx * 0.28));

                string left = string.Format(CultureInfo.InvariantCulture, "{0}  {1} / {2}  ({3:0}%)",
                    d[i].Label, CapShort(d[i].UsedGB), CapShort(d[i].TotalGB), d[i].Pct);
                string free = CapShort(d[i].FreeGB) + " free";

                Size freeSz = TextRenderer.MeasureText(g, free, lf);
                Rectangle freeRect = new Rectangle(pad + contentW - freeSz.Width, rowY, freeSz.Width, labelH);
                Rectangle leftRect = new Rectangle(pad, rowY, contentW - freeSz.Width - 6, labelH);

                TextRenderer.DrawText(g, left, lf, leftRect, TextColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, free, lf, freeRect, DimColor,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                int barY = rowY + labelH + gap;
                Rectangle track = new Rectangle(pad, barY, contentW, barH);
                using (SolidBrush tb = new SolidBrush(Track)) g.FillRectangle(tb, track);

                double pct = d[i].Pct; if (pct < 0) pct = 0; else if (pct > 100) pct = 100;
                int fillW = (int)(contentW * pct / 100.0);
                if (fillW > 0)
                {
                    Color fc = pct >= 90 ? NearFull : Accent;
                    using (SolidBrush fb = new SolidBrush(fc)) g.FillRectangle(fb, new Rectangle(pad, barY, fillW, barH));
                }
                using (Pen bp = new Pen(Color.FromArgb(70, 70, 78)))
                    g.DrawRectangle(bp, track.X, track.Y, track.Width - 1, track.Height - 1);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { if (_hdr != null) _hdr.Dispose(); if (_lbl != null) _lbl.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // One-line section, e.g. session network totals. The form sets the full text.
    internal sealed class NetTotalsPanel : InfoPanelBase
    {
        public string Line = "";

        public int PreferredHeight() { return LineH(Font) + Pad(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            int pad = Math.Max(4, Width / 24);
            Rectangle r = new Rectangle(pad, 0, Width - 2 * pad, Height);
            TextRenderer.DrawText(g, Line, Font, r, TextColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    // Header + up to 5 "name   value" rows. Used for Top CPU and Top RAM.
    internal sealed class ListPanel : InfoPanelBase
    {
        public string Header = "";
        public ProcEntry[] Rows = new ProcEntry[0];
        public bool IsBytes;   // true: value is RAM bytes; false: value is CPU percent
        private const int MaxRows = 5;

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

        public int PreferredHeight() { return (1 + MaxRows) * LineH(Font) + Pad(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            int pad = Math.Max(4, Width / 24);
            int contentW = Width - 2 * pad;
            int lh = LineH(Font);

            TextRenderer.DrawText(g, Header, HeaderFont(), new Rectangle(pad, 0, contentW, lh), DimColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            ProcEntry[] rows = Rows == null ? new ProcEntry[0] : Rows;
            for (int i = 0; i < MaxRows && i < rows.Length; i++)
            {
                int y = (i + 1) * lh;
                string val = IsBytes ? Bytes(rows[i].Value)
                                     : string.Format(CultureInfo.InvariantCulture, "{0:0}%", rows[i].Value);
                Size vsz = TextRenderer.MeasureText(g, val, Font);
                Rectangle vr = new Rectangle(pad + contentW - vsz.Width, y, vsz.Width, lh);
                Rectangle nr = new Rectangle(pad, y, contentW - vsz.Width - 6, lh);
                TextRenderer.DrawText(g, rows[i].Name, Font, nr, TextColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, val, Font, vr, TextColor,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _hdr != null) _hdr.Dispose();
            base.Dispose(disposing);
        }
    }

    // Header + LAN IP + (optional) external/WAN IP.
    internal sealed class IpPanel : InfoPanelBase
    {
        public string Lan = "—";
        public string Wan = "—";
        public bool ShowWan = true;

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

        public int PreferredHeight()
        {
            int lines = 1 + 1 + (ShowWan ? 1 : 0); // header + LAN + WAN?
            return lines * LineH(Font) + Pad();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            int pad = Math.Max(4, Width / 24);
            int contentW = Width - 2 * pad;
            int lh = LineH(Font);

            TextRenderer.DrawText(g, "IP", HeaderFont(), new Rectangle(pad, 0, contentW, lh), DimColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "LAN:  " + Lan, Font, new Rectangle(pad, lh, contentW, lh), TextColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (ShowWan)
                TextRenderer.DrawText(g, "WAN:  " + Wan, Font, new Rectangle(pad, lh * 2, contentW, lh), TextColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _hdr != null) _hdr.Dispose();
            base.Dispose(disposing);
        }
    }
}
