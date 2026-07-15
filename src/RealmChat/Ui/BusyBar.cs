using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RealmChat
{
    // Thin indeterminate progress bar. The engine reports lines, not percent,
    // so this shows "working" without lying about progress. Paints nothing
    // when inactive (keeps its layout slot; the window never reflows).
    public class BusyBar : Control, IThemed
    {
        private readonly Timer timer = new Timer { Interval = 16 };
        private Palette pal = Theme.Current;
        private float phase = -0.4f;

        public BusyBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 4;
            TabStop = false;
            AccessibleRole = AccessibleRole.ProgressBar;
            AccessibleName = "Working";
            timer.Tick += delegate
            {
                phase += 0.014f;
                if (phase > 1.4f) phase = -0.4f;
                Invalidate();
            };
        }

        public void ApplyTheme(Palette p) { pal = p; Invalidate(); }

        public bool Active
        {
            get { return timer.Enabled; }
            set
            {
                if (timer.Enabled == value) return;
                phase = -0.4f;
                timer.Enabled = value;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : pal.Background);
            if (!Active) return;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            var track = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Ui.RoundedRect(track, Height / 2))
            using (var b = new SolidBrush(Ui.Mix(pal.Accent, pal.Background, 0.8f)))
                g.FillPath(b, path);

            int segW = Math.Max(Ui.Dpi(this, 40), Width * 2 / 5);
            int x = (int)(phase * Width);
            var seg = Rectangle.Intersect(track, new Rectangle(x, 0, segW, Height - 1));
            if (seg.Width <= 0) return;
            using (var path = Ui.RoundedRect(seg, Height / 2))
            using (var b = new SolidBrush(pal.Accent))
                g.FillPath(b, path);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }
}

