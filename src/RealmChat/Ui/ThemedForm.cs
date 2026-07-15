using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RealmChat
{
    // Custom controls opt into theming by implementing this; the Styler calls
    // it on every theme change so controls never poll Theme.Current mid-paint.
    public interface IThemed
    {
        void ApplyTheme(Palette p);
    }

    // Base for both windows: applies the palette to itself and every child
    // (including the title bar), and re-applies live when the theme changes -
    // either the in-app toggle or the Windows app-mode setting in Auto mode.
    public class ThemedForm : Form
    {
        private const int WM_SETTINGCHANGE = 0x001A;

        protected ThemedForm()
        {
            AutoScaleMode = AutoScaleMode.Font;
            Font = Fonts.Body;
            DoubleBuffered = true;
            var ico = AppIcon.Get();
            if (ico != null) Icon = ico;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.Changed += ApplyTheme;
            ApplyTheme();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            Theme.Changed -= ApplyTheme;
            base.OnHandleDestroyed(e);
        }

        protected void ApplyTheme()
        {
            var p = Theme.Current;
            Native.DarkTitleBar(Handle, p.Dark);
            BackColor = p.Background;
            ForeColor = p.Text;
            Styler.Apply(this, p);
            OnThemeApplied(p);
            Invalidate(true);
        }

        // Hook for form-specific styling (e.g. semantic label colors).
        protected virtual void OnThemeApplied(Palette p) { }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_SETTINGCHANGE && m.LParam != IntPtr.Zero &&
                Marshal.PtrToStringAuto(m.LParam) == "ImmersiveColorSet")
                Theme.SystemChanged();
        }
    }

    // Themes the stock WinForms controls; IThemed controls style themselves.
    public static class Styler
    {
        public static void Apply(Control root, Palette p)
        {
            foreach (Control c in root.Controls)
            {
                // IThemed controls own their whole subtree (e.g. LogBox styles
                // its inner TextBox and placeholder itself) - don't descend.
                var themed = c as IThemed;
                if (themed != null) { themed.ApplyTheme(p); continue; }
                ApplyOne(c, p);
                Apply(c, p);
            }
        }

        private static void ApplyOne(Control c, Palette p)
        {
            var link = c as LinkLabel;
            if (link != null)
            {
                link.BackColor = Color.Transparent;
                link.LinkColor = p.Link;
                link.ActiveLinkColor = p.Accent;
                link.VisitedLinkColor = p.Link;
                return;
            }

            var text = c as TextBoxBase;
            if (text != null)
            {
                text.BackColor = p.Surface;
                text.ForeColor = p.Text;
                Native.ThemeScrollbars(text, p.Dark);
                return;
            }

            var combo = c as ComboBox;
            if (combo != null)
            {
                combo.BackColor = p.Surface;
                combo.ForeColor = p.Text;
                combo.FlatStyle = p.Dark ? FlatStyle.Flat : FlatStyle.Standard;
                Native.ThemeScrollbars(combo, p.Dark);
                return;
            }

            if (c is Label)
            {
                c.BackColor = Color.Transparent;
                c.ForeColor = p.Text;
                return;
            }

            // Controls inside a card sit on the Surface color, not the form's.
            c.BackColor = (c.Parent is CardPanel) ? p.Surface : p.Background;
            c.ForeColor = p.Text;
        }
    }

    // Shared typography scale: one place so both windows stay in step.
    public static class Fonts
    {
        public static readonly Font Body = Make("Segoe UI", 9.75f, FontStyle.Regular);
        public static readonly Font Caption = Make("Segoe UI", 8.5f, FontStyle.Regular);
        public static readonly Font Value = Make("Segoe UI Semibold", 9.75f, FontStyle.Regular);
        public static readonly Font Title = Make("Segoe UI Semibold", 15f, FontStyle.Regular);
        public static readonly Font Mono = Make("Consolas", 9f, FontStyle.Regular);

        private static Font Make(string family, float size, FontStyle style)
        {
            try
            {
                var f = new Font(family, size, style);
                // GDI falls back to a default family silently; detect and bold instead.
                if (family.EndsWith("Semibold") && f.Name != family)
                    return new Font("Segoe UI", size, FontStyle.Bold);
                return f;
            }
            catch { return new Font(FontFamily.GenericSansSerif, size, style); }
        }
    }

    // Secondary text: captions, hints, footers. Tracks the muted token.
    public class MutedLabel : Label, IThemed
    {
        public MutedLabel()
        {
            AutoSize = true;
            BackColor = Color.Transparent;
            Font = Fonts.Caption;
        }
        public void ApplyTheme(Palette p) { ForeColor = p.TextMuted; }
    }

    // The loot-chest icon embedded in the exe (app.ico), shared by every
    // window and the toast NotifyIcon. Null if the resource is missing -
    // callers fall back to the stock icon.
    public static class AppIcon
    {
        private static Icon icon;
        private static bool loaded;

        public static Icon Get()
        {
            if (!loaded)
            {
                loaded = true;
                try
                {
                    var s = typeof(AppIcon).Assembly.GetManifestResourceStream("RealmChat.app.ico");
                    if (s != null) using (s) icon = new Icon(s);
                }
                catch { }
            }
            return icon;
        }
    }
}

