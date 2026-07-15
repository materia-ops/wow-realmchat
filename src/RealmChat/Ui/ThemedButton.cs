using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RealmChat
{
    // Flat rounded button, custom-painted so both themes get identical
    // hover / pressed / disabled / focus states. Primary = filled accent
    // (the one main action per window); default = bordered neutral.
    public class ThemedButton : Button, IThemed
    {
        private Palette pal = Theme.Current;
        private bool hover, pressed;

        public bool Primary { get; set; }

        public ThemedButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
        }

        public void ApplyTheme(Palette p) { pal = p; Invalidate(); }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        // Mirror the pressed visual for keyboard activation (Space).
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) { pressed = true; Invalidate(); }
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) { pressed = false; Invalidate(); }
            base.OnKeyUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : pal.Background);

            var r = ClientRectangle;
            r.Width--; r.Height--;
            int radius = Ui.Dpi(this, 7);

            Color fill, textColor, border = Color.Empty;
            if (Primary)
            {
                fill = !Enabled ? Ui.Mix(pal.Accent, pal.Background, 0.55f)
                     : pressed ? pal.AccentPressed
                     : hover ? pal.AccentHover
                     : pal.Accent;
                textColor = Enabled ? pal.AccentText : Ui.Mix(pal.AccentText, fill, 0.35f);
            }
            else
            {
                fill = !Enabled ? pal.ButtonFace
                     : pressed ? pal.ButtonPressed
                     : hover ? pal.ButtonHover
                     : pal.ButtonFace;
                textColor = Enabled ? pal.Text : pal.TextMuted;
                border = pal.Border;
            }

            using (var path = Ui.RoundedRect(r, radius))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                if (border != Color.Empty)
                    using (var pen = new Pen(border)) g.DrawPath(pen, path);
            }

            if (Focused && ShowFocusCues)
            {
                var fr = r; fr.Inflate(-Ui.Dpi(this, 3), -Ui.Dpi(this, 3));
                using (var path = Ui.RoundedRect(fr, Math.Max(1, radius - 2)))
                using (var pen = new Pen(Primary ? pal.AccentText : pal.Accent) { DashStyle = DashStyle.Dot })
                    g.DrawPath(pen, path);
            }

            TextRenderer.DrawText(g, Text, Font, ClientRectangle, textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
    }
}

