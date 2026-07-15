using System;
using System.Drawing;
using System.Windows.Forms;

namespace RealmChat
{
    // The activity log: a bordered surface wrapping a read-only TextBox, with
    // an empty-state hint until the first line arrives. Keyboard-focusable so
    // the log can be scrolled and copied without a mouse.
    public class LogBox : Panel, IThemed
    {
        private readonly TextBox box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            AccessibleName = "Activity log",
        };

        private readonly Label placeholder = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "No activity yet.",
        };

        private Palette pal = Theme.Current;

        public LogBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Padding = new Padding(8, 6, 2, 6);
            Controls.Add(box);
            Controls.Add(placeholder);
            placeholder.BringToFront();
        }

        public string PlaceholderText
        {
            get { return placeholder.Text; }
            set { placeholder.Text = value; }
        }

        public void Append(string line)
        {
            if (placeholder.Visible) placeholder.Visible = false;
            box.AppendText(line + Environment.NewLine);
        }

        public void ApplyTheme(Palette p)
        {
            pal = p;
            BackColor = p.Surface;
            box.BackColor = p.Surface;
            box.ForeColor = p.Text;
            placeholder.BackColor = p.Surface;
            placeholder.ForeColor = p.TextMuted;
            Native.ThemeScrollbars(box, p.Dark);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(pal.Border))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }
}

