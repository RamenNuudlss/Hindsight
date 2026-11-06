using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Media;
using System.Windows.Forms;

namespace Hindsight
{
    /// <summary>The "clip saved" sound, synthesized at startup so there's no audio file to ship.</summary>
    internal static class Chime
    {
        static byte[] wav;

        static byte[] Build()
        {
            const int rate = 44100;
            // Two soft bell tones, a fourth apart, rising: reads as "got it".
            double[][] notes = { new[] { 1046.5, 0.0, 0.16 }, new[] { 1396.9, 0.075, 0.30 } };
            int total = (int)(rate * 0.42);
            var samples = new double[total];
            foreach (var n in notes)
            {
                int start = (int)(n[1] * rate), len = (int)(n[2] * rate);
                for (int i = 0; i < len && start + i < total; i++)
                {
                    double t = i / (double)rate;
                    double env = Math.Min(1.0, t / 0.004) * Math.Exp(-t * 9.0);
                    double v = Math.Sin(2 * Math.PI * n[0] * t) + 0.28 * Math.Sin(2 * Math.PI * n[0] * 2 * t) + 0.08 * Math.Sin(2 * Math.PI * n[0] * 3 * t);
                    samples[start + i] += v * env * 0.22;
                }
            }
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                int dataLen = total * 2;
                w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + dataLen); w.Write(new[] { 'W', 'A', 'V', 'E' });
                w.Write(new[] { 'f', 'm', 't', ' ' }); w.Write(16); w.Write((short)1); w.Write((short)1);
                w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(dataLen);
                foreach (var s in samples) w.Write((short)Math.Max(short.MinValue, Math.Min(short.MaxValue, s * short.MaxValue)));
                return ms.ToArray();
            }
        }

        public static void Play()
        {
            try
            {
                if (wav == null) wav = Build();
                var p = new SoundPlayer(new MemoryStream(wav));
                p.Play();
            }
            catch (Exception ex) { Log.Warn("chime: " + ex.Message); }
        }
    }

    /// <summary>A small notification in the corner that never steals focus from the game.</summary>
    internal sealed class Toast : Form
    {
        static Toast current;
        readonly string title, detail;
        readonly bool error;
        readonly Timer timer = new Timer { Interval = 16 };
        DateTime shownAt;
        const int Hold = 2600;
        static readonly Color Ink = Color.FromArgb(29, 26, 44);
        static readonly Color Amber = Color.FromArgb(255, 179, 71);
        static readonly Color Red = Color.FromArgb(255, 95, 109);
        static readonly Color Text1 = Color.FromArgb(236, 233, 245);
        static readonly Color Muted = Color.FromArgb(155, 149, 180);

        public static void Show(string title, string detail, bool error = false, IntPtr nearWindow = default(IntPtr))
        {
            try
            {
                current?.Close();
                current = new Toast(title, detail, error, nearWindow);
                current.Show();
            }
            catch (Exception ex) { Log.Warn("toast: " + ex.Message); }
        }

        Toast(string title, string detail, bool error, IntPtr near)
        {
            this.title = title; this.detail = detail; this.error = error;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Ink;
            DoubleBuffered = true;
            Opacity = 0;

            var screen = near != IntPtr.Zero ? Screen.FromHandle(near) : Screen.PrimaryScreen;
            float scale = Native.DpiScaleFor(near != IntPtr.Zero ? near : IntPtr.Zero);
            if (scale < 1) scale = 1;
            int w = (int)(340 * scale), h = (int)(72 * scale);
            Size = new Size(w, h);
            var wa = screen.WorkingArea;
            Location = new Point(wa.Right - w - (int)(20 * scale), wa.Top + (int)(20 * scale));
            dpi = scale;

            timer.Tick += (s, e) => Step();
        }

        float dpi;

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST | Native.WS_EX_TRANSPARENT;
                cp.ClassStyle |= Native.CS_DROPSHADOW;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.RoundCorners(Handle);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            shownAt = DateTime.UtcNow;
            timer.Start();
        }

        void Step()
        {
            double ms = (DateTime.UtcNow - shownAt).TotalMilliseconds;
            if (ms < 140) Opacity = ms / 140 * 0.97;
            else if (ms < Hold) Opacity = 0.97;
            else if (ms < Hold + 320) Opacity = 0.97 * (1 - (ms - Hold) / 320);
            else { timer.Stop(); Close(); }
        }

        static Font MakeFont(float size, FontStyle style)
        {
            string[] families = { "Segoe UI Variable Text", "Segoe UI" };
            var installed = new InstalledFontCollection().Families.Select(f => f.Name).ToList();
            string fam = families.FirstOrDefault(installed.Contains) ?? FontFamily.GenericSansSerif.Name;
            return new Font(fam, size, style, GraphicsUnit.Pixel);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            float s = dpi;
            g.Clear(Ink);
            // Accent: a tiny "tape" mark — amber for saved, red for problems
            var accent = error ? Red : Amber;
            using (var b = new SolidBrush(accent))
                g.FillEllipse(b, 20 * s, ClientSize.Height / 2f - 6 * s, 12 * s, 12 * s);
            using (var b = new SolidBrush(Color.FromArgb(60, accent)))
                g.FillEllipse(b, 16 * s, ClientSize.Height / 2f - 10 * s, 20 * s, 20 * s);
            using (var f1 = MakeFont(15 * s, FontStyle.Bold))
            using (var f2 = MakeFont(13 * s, FontStyle.Regular))
            using (var b1 = new SolidBrush(Text1))
            using (var b2 = new SolidBrush(Muted))
            {
                var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
                float x = 50 * s, wTxt = ClientSize.Width - x - 16 * s;
                g.DrawString(title, f1, b1, new RectangleF(x, 15 * s, wTxt, 22 * s), fmt);
                g.DrawString(detail ?? "", f2, b2, new RectangleF(x, 38 * s, wTxt, 20 * s), fmt);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
