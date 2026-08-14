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

        // Painted once a second per panel, five panels: creating a pen, a brush and two point
        // arrays every time meant a steady stream of GDI handle churn and garbage for a picture
        // that mostly doesn't change. All of it is cached and only rebuilt when the colour (or the
        // sample count, which stops changing after the first minute) actually differs.
        private static readonly Pen GridPen = new Pen(GridColor);
        private readonly Pen[] _pens = new Pen[2];
        private readonly Color[] _penColors = new Color[2];
        private readonly SolidBrush[] _brushes = new SolidBrush[2];
        private readonly Color[] _brushColors = new Color[2];
        private PointF[] _ptsBuf;
        private PointF[] _polyBuf;

        private Pen SeriesPen(int slot, Color c)
        {
            if (_pens[slot] == null || _penColors[slot] != c)
            {
                if (_pens[slot] != null) _pens[slot].Dispose();
                _pens[slot] = new Pen(c, 1.5f);
                _penColors[slot] = c;
            }
            return _pens[slot];
        }

        private SolidBrush FillBrush(int slot, Color c)
        {
            Color faded = Color.FromArgb(85, c);
            if (_brushes[slot] == null || _brushColors[slot] != faded)
            {
                if (_brushes[slot] != null) _brushes[slot].Dispose();
                _brushes[slot] = new SolidBrush(faded);
                _brushColors[slot] = faded;
            }
            return _brushes[slot];
        }

        public string Title = "";
        public string ValueText = "";
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

            // Value readout, right-aligned. Temperatures used to be appended here as a suffix; they
            // have their own tile section now, so showing them twice was just noise.
            int right = r.Right - pad;
            Size vsz = TextRenderer.MeasureText(g, ValueText, Font);
            TextRenderer.DrawText(g, ValueText, Font, new Point(right - vsz.Width, pad),
                ValueColor, TextFormatFlags.NoPadding);

            Rectangle gr = new Rectangle(pad, pad + headerH, r.Width - 2 * pad, r.Height - headerH - 2 * pad);
            if (gr.Width < 4 || gr.Height < 4) return;

            for (int i = 1; i < 4; i++)
            {
                int y = gr.Top + gr.Height * i / 4;
                g.DrawLine(GridPen, gr.Left, y, gr.Right, y);
            }
            g.DrawRectangle(GridPen, gr.Left, gr.Top, gr.Width - 1, gr.Height - 1);

            if (!Available)
            {
                TextRenderer.DrawText(g, "n/a", Font, gr, DimColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            double max = FixedMax > 0 ? FixedMax : (Percent ? 100.0 : NiceMax(VisibleMax()));

            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawSeries(g, gr, _a, max, seriesA, true, 0);
            if (_two) DrawSeries(g, gr, _b, max, seriesB, false, 1);
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

        // slot selects which cached pen/brush pair to use (0 = series A, 1 = the upload series).
        // GDI+ has no count-limited DrawLines/FillPolygon overload, so the buffers must be exactly
        // as long as the data — but _count stops changing once the history fills, so in steady
        // state these are allocated once and reused forever.
        private void DrawSeries(Graphics g, Rectangle gr, double[] data, double max, Color color,
            bool fill, int slot)
        {
            if (_count < 1 || max <= 0) return;
            int start = Capacity - _count;
            float denom = (_count > 1) ? (_count - 1) : 1f;

            if (_ptsBuf == null || _ptsBuf.Length != _count) _ptsBuf = new PointF[_count];
            PointF[] pts = _ptsBuf;
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
                    if (_polyBuf == null || _polyBuf.Length != _count + 2) _polyBuf = new PointF[_count + 2];
                    PointF[] poly = _polyBuf;
                    Array.Copy(pts, poly, _count);
                    poly[_count] = new PointF(pts[_count - 1].X, gr.Bottom - 1);
                    poly[_count + 1] = new PointF(pts[0].X, gr.Bottom - 1);
                    g.FillPolygon(FillBrush(slot, color), poly);
                }
                g.DrawLines(SeriesPen(slot, color), pts);
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
            if (disposing)
            {
                if (_titleFont != null) _titleFont.Dispose();
                for (int i = 0; i < 2; i++)
                {
                    if (_pens[i] != null) _pens[i].Dispose();
                    if (_brushes[i] != null) _brushes[i].Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
