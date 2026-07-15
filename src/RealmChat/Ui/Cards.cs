using System;
using System.Drawing;
using System.Windows.Forms;

namespace RealmChat
{
    // Rounded surface container: the backbone of the polished layout. Children
    // sit on the Surface color; the panel styles its own subtree (Styler does
    // not descend into IThemed controls).
    public class CardPanel : Panel, IThemed
    {
        private Palette pal = Theme.Current;

        public CardPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Padding = new Padding(14);
        }

        public void ApplyTheme(Palette p)
        {
            pal = p;
            BackColor = p.Surface;
            ForeColor = p.Text;
            Styler.Apply(this, p);
            Invalidate(true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : pal.Background);
            var r = ClientRectangle;
            r.Width--; r.Height--;
            using (var path = Ui.RoundedRect(r, Ui.Dpi(this, 8)))
            {
                using (var b = new SolidBrush(pal.Surface)) g.FillPath(b, path);
                using (var pen = new Pen(pal.Border)) g.DrawPath(pen, path);
            }
        }
    }

    // Flat 2-5 segment picker (used for the theme switch). Custom painted so
    // both palettes get identical hover/selected states.
    public class ThemedSegmented : Control, IThemed
    {
        private Palette pal = Theme.Current;
        private string[] items = new string[0];
        private int selected = -1;
        private int hover = -1;

        public event Action<int> SelectedChanged;

        public ThemedSegmented()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 28;
            TabStop = true;
        }

        public string[] Items
        {
            get { return items; }
            set { items = value ?? new string[0]; Invalidate(); }
        }

        public int SelectedIndex
        {
            get { return selected; }
            set
            {
                if (value == selected) return;
                selected = value;
                Invalidate();
            }
        }

        public void ApplyTheme(Palette p) { pal = p; Invalidate(); }

        private int HitTest(int x)
        {
            if (items.Length == 0) return -1;
            int w = Width / items.Length;
            int i = Math.Min(items.Length - 1, Math.Max(0, x / Math.Max(1, w)));
            return i;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitTest(e.X);
            if (h != hover) { hover = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = -1; Invalidate(); base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int h = HitTest(e.X);
            if (h >= 0 && h != selected)
            {
                selected = h;
                Invalidate();
                var ev = SelectedChanged;
                if (ev != null) ev(h);
            }
            base.OnMouseDown(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Left || keyData == Keys.Right || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int next = selected;
            if (e.KeyCode == Keys.Left) next = Math.Max(0, selected - 1);
            else if (e.KeyCode == Keys.Right) next = Math.Min(items.Length - 1, selected + 1);
            if (next != selected)
            {
                selected = next;
                Invalidate();
                var ev = SelectedChanged;
                if (ev != null) ev(next);
            }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : pal.Background);
            var r = ClientRectangle;
            r.Width--; r.Height--;
            int radius = Ui.Dpi(this, 6);

            using (var path = Ui.RoundedRect(r, radius))
            {
                using (var b = new SolidBrush(pal.ButtonFace)) g.FillPath(b, path);
                using (var pen = new Pen(pal.Border)) g.DrawPath(pen, path);
            }

            if (items.Length == 0) return;
            float segW = (float)r.Width / items.Length;
            for (int i = 0; i < items.Length; i++)
            {
                var seg = new Rectangle((int)(r.X + i * segW), r.Y, (int)segW, r.Height);
                if (i == selected)
                {
                    var inner = seg;
                    inner.Inflate(-Ui.Dpi(this, 2), -Ui.Dpi(this, 2));
                    using (var path = Ui.RoundedRect(inner, radius - 2))
                    using (var b = new SolidBrush(pal.Accent))
                        g.FillPath(b, path);
                }
                else if (i == hover)
                {
                    var inner = seg;
                    inner.Inflate(-Ui.Dpi(this, 2), -Ui.Dpi(this, 2));
                    using (var path = Ui.RoundedRect(inner, radius - 2))
                    using (var b = new SolidBrush(pal.ButtonHover))
                        g.FillPath(b, path);
                }
                TextRenderer.DrawText(g, items[i], Font, seg,
                    i == selected ? pal.AccentText : pal.TextMuted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding);
            }

            if (Focused && ShowFocusCues)
                using (var pen = new Pen(pal.Accent) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot })
                    g.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
        }
    }
}
