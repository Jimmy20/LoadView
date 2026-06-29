using System;
using System.Drawing;
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
        protected static readonly Color TextColor = Color.FromArgb(232, 232, 237);
        protected static readonly Color DimColor = Color.FromArgb(150, 150, 158);

        protected InfoPanelBase()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = PanelBack;
        }
    }

    // Big centered clock (HH:mm:ss).
    internal sealed class ClockPanel : InfoPanelBase
    {
        public string TimeText = "";
        private Font _big;

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (_big != null) { _big.Dispose(); _big = null; }
        }

        private Font Big()
        {
            if (_big == null) _big = new Font(Font.FontFamily, Font.SizeInPoints * 2.2f, FontStyle.Bold);
            return _big;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            TextRenderer.DrawText(g, TimeText, Big(), ClientRectangle, TextColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _big != null) _big.Dispose();
            base.Dispose(disposing);
        }
    }

    // Date (DD.MM.YYYY) on top, weekday below — both centered.
    internal sealed class FooterPanel : InfoPanelBase
    {
        public string DateText = "";
        public string DayText = "";
        private Font _date, _day;

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (_date != null) { _date.Dispose(); _date = null; }
            if (_day != null) { _day.Dispose(); _day = null; }
        }

        private Font DateFont()
        {
            if (_date == null) _date = new Font(Font.FontFamily, Font.SizeInPoints * 1.4f, FontStyle.Bold);
            return _date;
        }

        private Font DayFont()
        {
            if (_day == null) _day = new Font(Font.FontFamily, Font.SizeInPoints * 1.05f, FontStyle.Regular);
            return _day;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            int h = ClientRectangle.Height;
            Rectangle top = new Rectangle(0, 0, Width, (int)(h * 0.56));
            Rectangle bot = new Rectangle(0, top.Bottom, Width, h - top.Bottom);
            TextRenderer.DrawText(g, DateText, DateFont(), top, TextColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, DayText, DayFont(), bot, DimColor,
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

    // Text-only list of drives: current capacity + usage (no graph).
    internal sealed class DrivesPanel : InfoPanelBase
    {
        public DriveLine[] Drives = new DriveLine[0];
        private Font _hdr;

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (_hdr != null) { _hdr.Dispose(); _hdr = null; }
        }

        private Font HeaderFont()
        {
            if (_hdr == null) _hdr = new Font(Font, FontStyle.Bold);
            return _hdr;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);

            DriveLine[] d = Drives == null ? new DriveLine[0] : Drives;
            int slices = d.Length + 1; // header + one row per drive
            float sliceH = (float)ClientRectangle.Height / slices;
            int pad = Math.Max(4, Width / 24);

            Rectangle hr = new Rectangle(pad, 0, Width - 2 * pad, (int)sliceH);
            TextRenderer.DrawText(g, "DRIVES", HeaderFont(), hr, DimColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            for (int i = 0; i < d.Length; i++)
            {
                Rectangle rr = new Rectangle(pad, (int)((i + 1) * sliceH), Width - 2 * pad, (int)sliceH);
                TextRenderer.DrawText(g, FormatDrive(d[i]), Font, rr, TextColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
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
