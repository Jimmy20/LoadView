using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace LoadView
{
    // A row (or several) of small labelled squares, one per sensor: the label on top, the reading
    // under it. Used for temperatures and for fan speeds, which is why the unit is a property.
    //
    // Temperatures used to be a suffix tacked onto the CPU and GPU graph headers, where they were
    // easy to miss entirely - this gives them a section of their own.
    internal sealed class TilePanel : InfoPanelBase
    {
        public string Header = "TEMPERATURES";
        public SensorReading[] Items = new SensorReading[0];

        public float LabelSize = 8f;
        public float ValueSize = 13f;
        public int TilePx = 46;          // tile height; auto columns aim for roughly square tiles
        public int ColumnsWanted = 0;    // 0 = as many as fit
        public double HotC = 0;          // >0: a temperature at or above this draws red
        public bool Fahrenheit;
        public bool ShowHeader = true;

        private static readonly Color TileBack = Color.FromArgb(38, 38, 44);
        private static readonly Color HotColor = Color.FromArgb(0xE0, 0x4F, 0x4F);

        private Font _labelFont, _valueFont, _headerFont;
        private float _builtLabel, _builtValue;

        private Font LabelFont()
        {
            if (_labelFont == null || _builtLabel != LabelSize)
            {
                if (_labelFont != null) _labelFont.Dispose();
                _labelFont = new Font(Font.FontFamily, LabelSize);
                _builtLabel = LabelSize;
            }
            return _labelFont;
        }

        private Font ValueFont()
        {
            if (_valueFont == null || _builtValue != ValueSize)
            {
                if (_valueFont != null) _valueFont.Dispose();
                _valueFont = new Font(Font.FontFamily, ValueSize, FontStyle.Bold);
                _builtValue = ValueSize;
            }
            return _valueFont;
        }

        private Font HeaderFont()
        {
            if (_headerFont == null) _headerFont = new Font(Font.FontFamily, 7.5f, FontStyle.Bold);
            return _headerFont;
        }

        // Zero when there is nothing to show, so OverlayForm can leave the section out altogether
        // rather than drawing an empty frame -- which is what a machine with no readable sensor gets.
        public int ContentHeight()
        {
            int n = Items == null ? 0 : Items.Length;
            if (n == 0) return 0;

            int gap = Gap();
            int cols = Columns(n);
            int rows = (n + cols - 1) / cols;
            int h = rows * TileScaled() + (rows - 1) * gap;
            if (ShowHeader) h += HeaderH();
            return h + TilePad();
        }

        private int TilePad() { return (int)(6 * DeviceDpi / 96f); }
        private int Gap() { return (int)(4 * DeviceDpi / 96f); }
        private int TileScaled() { return (int)(TilePx * DeviceDpi / 96f); }
        private int HeaderH() { return LineH(HeaderFont()) + (int)(2 * DeviceDpi / 96f); }

        private int Columns(int n)
        {
            if (ColumnsWanted > 0) return Math.Min(ColumnsWanted, n);
            int pad = TilePad(), gap = Gap(), tile = TileScaled();
            int usable = Width - 2 * pad;
            if (usable < tile) return 1;
            // Aim for tiles about as wide as they are tall, so they read as squares.
            int fit = (usable + gap) / (tile + gap);
            if (fit < 1) fit = 1;
            return Math.Min(fit, n);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            int n = Items == null ? 0 : Items.Length;
            if (n == 0) return;

            int pad = TilePad(), gap = Gap(), tile = TileScaled();
            int y = 0;
            if (ShowHeader)
            {
                TextRenderer.DrawText(g, Header, HeaderFont(), new Point(pad, 0), DimColor,
                    TextFormatFlags.NoPadding);
                y = HeaderH();
            }

            int cols = Columns(n);
            int usable = Width - 2 * pad;

            // Square tiles, not stretched to fill the row: with two sensors on a narrow overlay,
            // filling the width turned them into wide rectangles rather than the little squares this
            // is meant to be. They are as wide as they are tall, and the row is centred - which also
            // means a row of 2 and a row of 4 look like the same component.
            int tileW = tile;
            int maxW = (usable - (cols - 1) * gap) / cols;
            if (tileW > maxW) tileW = maxW;      // very narrow window: fall back to fitting
            if (tileW < 8) tileW = 8;

            int rowW = cols * tileW + (cols - 1) * gap;
            int x0 = pad + Math.Max(0, (usable - rowW) / 2);

            for (int i = 0; i < n; i++)
            {
                int col = i % cols, row = i / cols;
                Rectangle r = new Rectangle(x0 + col * (tileW + gap), y + row * (tile + gap), tileW, tile);
                DrawTile(g, r, Items[i]);
            }
        }

        private void DrawTile(Graphics g, Rectangle r, SensorReading s)
        {
            using (SolidBrush b = new SolidBrush(TileBack)) g.FillRectangle(b, r);

            bool hot = s.Kind == SensorKind.Temperature && HotC > 0 && s.Value >= HotC;
            Font lf = LabelFont(), vf = ValueFont();
            int lh = LineH(lf), vh = LineH(vf);
            int top = r.Top + Math.Max(1, (r.Height - lh - vh) / 2);

            Rectangle lr = new Rectangle(r.Left + 2, top, r.Width - 4, lh);
            TextRenderer.DrawText(g, s.Label, lf, lr, DimColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

            Rectangle vr = new Rectangle(r.Left + 2, top + lh, r.Width - 4, vh);
            TextRenderer.DrawText(g, Format(s), vf, vr, hot ? HotColor : TextColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        private string Format(SensorReading s)
        {
            if (s.Kind == SensorKind.Fan)
                return s.Value.ToString("0", CultureInfo.InvariantCulture);
            double v = Fahrenheit ? s.Value * 9.0 / 5.0 + 32.0 : s.Value;
            return v.ToString("0", CultureInfo.InvariantCulture) + "°";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_labelFont != null) _labelFont.Dispose();
                if (_valueFont != null) _valueFont.Dispose();
                if (_headerFont != null) _headerFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
