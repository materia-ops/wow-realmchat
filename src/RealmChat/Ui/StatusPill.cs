using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RealmChat
{
    public enum StatusKind { Neutral, Busy, Success, Info, Warning, Error }

    // Rounded status badge with a colored dot: the window's one-glance answer
    // to "what state am I in". Auto-sizes to its text; the text doubles as the
    // control's accessible name for screen readers.
    public class StatusPill : Control, IThemed
    {
        private Palette pal = Theme.Current;
        private StatusKind kind = StatusKind.Neutral;

        public StatusPill()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AccessibleRole = AccessibleRole.StaticText;
            TabStop = false;
            Font = Fonts.Value;
        }

        public void ApplyTheme(Palette p) { pal = p; Invalidate(); }

        public void SetStatus(StatusKind k, string text)
        {
            kind = k;
            Text = text;                       // triggers OnTextChanged below
            AccessibleName = "Status: " + text;
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Size = GetPreferredSize(Size.Empty);
            Invalidate();
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            var ts = TextRenderer.MeasureText(Text, Font);
            return new Size(ts.Width + Ui.Dpi(this, 34), Ui.Dpi(this, 28));
        }

        private Color StateColor()
        {
            switch (kind)
            {
                case StatusKind.Busy:
                case StatusKind.Info: return pal.Accent;
                case StatusKind.Success: return pal.Success;
                case StatusKind.Warning: return pal.Warning;
                case StatusKind.Error: return pal.Danger;
                default: return pal.TextMuted;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : pal.Background);

            var state = StateColor();
            var r = ClientRectangle;
            r.Width--; r.Height--;

            using (var path = Ui.RoundedRect(r, r.Height / 2))
            {
                using (var b = new SolidBrush(Color.FromArgb(pal.Dark ? 48 : 28, state)))
                    g.FillPath(b, path);
                using (var pen = new Pen(Color.FromArgb(pal.Dark ? 110 : 70, state)))
                    g.DrawPath(pen, path);
            }

            int dot = Ui.Dpi(this, 8);
            int dotX = Ui.Dpi(this, 12);
            int ring = Ui.Dpi(this, 3);
            // soft halo behind the dot lifts it off the tinted pill
            using (var b = new SolidBrush(Color.FromArgb(60, state)))
                g.FillEllipse(b, dotX - ring, (Height - dot) / 2 - ring, dot + ring * 2, dot + ring * 2);
            using (var b = new SolidBrush(state))
                g.FillEllipse(b, dotX, (Height - dot) / 2, dot, dot);

            var textRect = new Rectangle(dotX + dot + Ui.Dpi(this, 6), 0,
                Width - (dotX + dot + Ui.Dpi(this, 6)), Height);
            // Dot carries the state color; text stays high-contrast in both themes.
            TextRenderer.DrawText(g, Text, Font, textRect, pal.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}

