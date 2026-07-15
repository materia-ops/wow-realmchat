using System;
using System.Drawing;
using System.Windows.Forms;

namespace RealmChat
{
    // The activity log: a bordered surface wrapping a read-only RichTextBox
    // (per-line semantic colors), with an empty-state hint until the first
    // line arrives. Keyboard-focusable so it can be scrolled/copied.
    public class LogBox : Panel, IThemed
    {
        private readonly RichTextBox box = new RichTextBox
        {
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            Dock = DockStyle.Fill,
            AccessibleName = "Activity log",
            Font = Fonts.Mono,
            DetectUrls = false,
        };

        private readonly Label placeholder = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "No activity yet.",
        };

        private Palette pal = Theme.Current;

        // Inside a card the log reads as a recessed well: it takes the form's
        // Background color instead of Surface so it stands off the card.
        public bool Inset { get; set; }

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
            box.SelectionStart = box.TextLength;
            box.SelectionColor = ColorFor(line);
            box.AppendText(line + Environment.NewLine);
            box.SelectionStart = box.TextLength;
            box.ScrollToCaret();
        }

        // Semantic line tinting: errors pop, good news reads green, the rest
        // stays quiet.
        private Color ColorFor(string line)
        {
            if (line.IndexOf("FAILED", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("FIREWALL:", StringComparison.Ordinal) >= 0 ||
                line.IndexOf("Couldn't", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("failed", StringComparison.Ordinal) >= 0)
                return pal.Danger;
            if (line.IndexOf("ready", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("Updated", StringComparison.Ordinal) >= 0 ||
                line.IndexOf("verified", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("healthy again", StringComparison.Ordinal) >= 0)
                return pal.Success;
            return pal.Text;
        }

        public void ApplyTheme(Palette p)
        {
            pal = p;
            var back = Inset ? p.Background : p.Surface;
            BackColor = back;
            box.BackColor = back;
            box.ForeColor = p.Text;
            placeholder.BackColor = back;
            placeholder.ForeColor = p.TextMuted;
            Native.ThemeScrollbars(box, p.Dark);
            // Re-tint existing lines so a live theme switch doesn't strand
            // the old palette's colors.
            box.SelectAll();
            box.SelectionColor = p.Text;
            box.DeselectAll();
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

