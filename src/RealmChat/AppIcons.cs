using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace RealmChat
{
    // Runtime-drawn chat-bubble icons (no asset files, and the tray icon can
    // carry state color: gray = stopped, green = chat running). Drawn once,
    // cached for the process lifetime.
    public static class AppIcons
    {
        private static Icon neutral, running;

        public static Icon Neutral
        {
            get { return neutral ?? (neutral = Draw(Color.FromArgb(0x8A, 0x93, 0xA2))); }
        }

        public static Icon Running
        {
            get { return running ?? (running = Draw(Color.FromArgb(0x2E, 0xB0, 0x6E))); }
        }

        // The brand-blue mark (matches app.ico), for in-window use.
        private static Icon brand;
        public static Icon Brand
        {
            get { return brand ?? (brand = Draw(Color.FromArgb(0x25, 0x63, 0xEB))); }
        }

        private static Icon Draw(Color fill)
        {
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                // Rounded speech bubble with a tail, plus three "typing" dots.
                using (var body = new GraphicsPath())
                {
                    var r = new Rectangle(2, 3, 28, 20);
                    int d = 10;
                    body.AddArc(r.X, r.Y, d, d, 180, 90);
                    body.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                    body.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                    body.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                    body.CloseFigure();
                    body.AddPolygon(new[] { new Point(8, 22), new Point(8, 30), new Point(16, 22) });
                    using (var b = new SolidBrush(fill)) g.FillPath(b, body);
                }
                using (var b = new SolidBrush(Color.White))
                {
                    g.FillEllipse(b, 7, 11, 4, 4);
                    g.FillEllipse(b, 14, 11, 4, 4);
                    g.FillEllipse(b, 21, 11, 4, 4);
                }

                IntPtr h = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(h).Clone(); }
                finally { NativeMethods.DestroyIcon(h); }
            }
        }

        private static class NativeMethods
        {
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern bool DestroyIcon(IntPtr handle);
        }
    }
}
