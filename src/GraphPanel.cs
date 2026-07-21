using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LoadView
{
    // A single Task-Manager-style metric row: a title, a live numeric readout and a
    // scrolling graph. Supports one series (CPU/GPU/RAM/Disk) or two (network down/up),
    // and either a fixed 0..100 scale or an auto-scaled rate scale.
    internal sealed class GraphPanel : Control
    {
        private const int Capacity = 60; // ~1 minute of history at 1s

        private readonly double[] _a = new double[Capacity];
        private readonly double[] _b = new double[Capacity];
        private int _count;
        private readonly bool _two;

        private Font _titleFont;

        public string Title = "";
        public string ValueText = "";
        // Optional right-aligned suffix drawn to the right of ValueText in its own colour
        // (used for the temperature, so it can turn red independently of the % readout).
        public string ValueSuffix = "";
        public Color ValueSuffixColor = NormalValueColor;
        public Color Accent = Color.FromArgb(0x4F, 0x8C, 0xFF);
        public Color Accent2 = Color.FromArgb(0x55, 0xD6, 0xFF);
        public bool Percent = true;  // true: 0..100; false: auto-scaled rate
        public bool Available = true;
        public double MinScale = 1;  // floor for auto-scale
        public double FixedMax = 0;          // 0 = auto (or 100 for percent graphs)
        public double AlertThreshold = 0;    // 0 = off; when latest sample >= this, the graph turns red
        public Color AlertColor = Color.FromArgb(0xE0, 0x4F, 0x4F);

        private static readonly Color PanelBack = Color.FromArgb(26, 26, 30);
        private static readonly Color GridColor = Color.FromArgb(45, 45, 52);
        public static readonly Color NormalValueColor = Color.FromArgb(228, 228, 233);
        private static readonly Color ValueColor = NormalValueColor;
        private static readonly Color DimColor = Color.FromArgb(150, 150, 158);

        public GraphPanel(bool twoSeries)
        {
            _two = twoSeries;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = PanelBack;
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (_titleFont != null) { _titleFont.Dispose(); _titleFont = null; }
        }

        private Font TitleFont()
        {
            if (_titleFont == null) _titleFont = new Font(Font, FontStyle.Bold);
            return _titleFont;
        }

        public void Add(double value) { Add(value, 0); }

        public void Add(double a, double b)
        {
            Array.Copy(_a, 1, _a, 0, Capacity - 1);
            Array.Copy(_b, 1, _b, 0, Capacity - 1);
            _a[Capacity - 1] = a;
            _b[Capacity - 1] = b;
            if (_count < Capacity) _count++;
            Invalidate();
        }

        // Drop all history (used when the network unit changes, to avoid mixed-unit data).
        public void ClearHistory()
        {
            Array.Clear(_a, 0, Capacity);
            Array.Clear(_b, 0, Capacity);
            _count = 0;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);

            Rectangle r = ClientRectangle;
            int pad = Math.Max(4, r.Width / 40);
            int headerH = TitleFont().Height + 2;

            bool alert = Available && AlertThreshold > 0 && LatestPeak() >= AlertThreshold;
            Color seriesA = alert ? AlertColor : Accent;
            Color seriesB = alert ? AlertColor : Accent2;

            TextRenderer.DrawText(g, Title, TitleFont(), new Point(pad, pad),
                Available ? seriesA : DimColor, TextFormatFlags.NoPadding);

            // Value readout, right-aligned; an optional suffix (temperature) is drawn to its
            // right in its own colour so it can go red without recolouring the % value.
            int right = r.Right - pad;
            if (!string.IsNullOrEmpty(ValueSuffix))
            {
                Size ssz = TextRenderer.MeasureText(g, ValueSuffix, Font);
                TextRenderer.DrawText(g, ValueSuffix, Font, new Point(right - ssz.Width, pad),
                    ValueSuffixColor, TextFormatFlags.NoPadding);
                right -= ssz.Width;
            }
            Size vsz = TextRenderer.MeasureText(g, ValueText, Font);
            TextRenderer.DrawText(g, ValueText, Font, new Point(right - vsz.Width, pad),
                ValueColor, TextFormatFlags.NoPadding);

            Rectangle gr = new Rectangle(pad, pad + headerH, r.Width - 2 * pad, r.Height - headerH - 2 * pad);
            if (gr.Width < 4 || gr.Height < 4) return;

            using (Pen grid = new Pen(GridColor))
            {
                for (int i = 1; i < 4; i++)
                {
                    int y = gr.Top + gr.Height * i / 4;
                    g.DrawLine(grid, gr.Left, y, gr.Right, y);
                }
                g.DrawRectangle(grid, gr.Left, gr.Top, gr.Width - 1, gr.Height - 1);
            }

            if (!Available)
            {
                TextRenderer.DrawText(g, "n/a", Font, gr, DimColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            double max = FixedMax > 0 ? FixedMax : (Percent ? 100.0 : NiceMax(VisibleMax()));

            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawSeries(g, gr, _a, max, seriesA, true);
            if (_two) DrawSeries(g, gr, _b, max, seriesB, false);
            // Nothing is drawn inside the plot area itself — the title/value live in the header above.
        }

        // The most recent sample (max of both series for the two-series network graph).
        private double LatestPeak()
        {
            if (_count < 1) return 0;
            double v = _a[Capacity - 1];
            if (_two && _b[Capacity - 1] > v) v = _b[Capacity - 1];
            return v;
        }

        private double VisibleMax()
        {
            double m = MinScale;
            int start = Capacity - _count;
            for (int i = start; i < Capacity; i++)
            {
                if (_a[i] > m) m = _a[i];
                if (_two && _b[i] > m) m = _b[i];
            }
            return m;
        }

        private void DrawSeries(Graphics g, Rectangle gr, double[] data, double max, Color color, bool fill)
        {
            if (_count < 1 || max <= 0) return;
            int start = Capacity - _count;
            float denom = (_count > 1) ? (_count - 1) : 1f;

            PointF[] pts = new PointF[_count];
            for (int i = start; i < Capacity; i++)
            {
                double frac = data[i] / max;
                if (frac < 0) frac = 0; else if (frac > 1) frac = 1;
                float x = gr.Left + (gr.Width - 1) * (i - start) / denom;
                float y = gr.Bottom - 1 - (float)(frac * (gr.Height - 2));
                pts[i - start] = new PointF(x, y);
            }

            if (_count >= 2)
            {
                if (fill)
                {
                    PointF[] poly = new PointF[_count + 2];
                    Array.Copy(pts, poly, _count);
                    poly[_count] = new PointF(pts[_count - 1].X, gr.Bottom - 1);
                    poly[_count + 1] = new PointF(pts[0].X, gr.Bottom - 1);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(85, color)))
                        g.FillPolygon(b, poly);
                }
                using (Pen p = new Pen(color, 1.5f))
                    g.DrawLines(p, pts);
            }
        }

        // Round a value up to a "nice" 1/2/5 x 10^n axis maximum.
        private static double NiceMax(double max)
        {
            if (max <= 0) return 1;
            double exp = Math.Floor(Math.Log10(max));
            double f = max / Math.Pow(10, exp);
            double nice;
            if (f <= 1) nice = 1;
            else if (f <= 2) nice = 2;
            else if (f <= 5) nice = 5;
            else nice = 10;
            return nice * Math.Pow(10, exp);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _titleFont != null) _titleFont.Dispose();
            base.Dispose(disposing);
        }
    }
}
