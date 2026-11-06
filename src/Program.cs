using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Hindsight")]
[assembly: AssemblyDescription("Save the last few minutes of gameplay with one key")]
[assembly: AssemblyCompany("RamenNuudls")]
[assembly: AssemblyProduct("Hindsight")]
[assembly: AssemblyCopyright("Copyright © 2026 RamenNuudls")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace Hindsight
{
    internal static class Program
    {
        public const string Version = "1.0.0";
        static Mutex instance;

        [STAThread]
        static int Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += Embedded.Resolve;
            return Run(args);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static int Run(string[] args)
        {
            Log.Init();
            Log.Info("start " + Version + " args=[" + string.Join(" ", args) + "] elevated=" + SystemTweaks.IsElevated);

            // The UAC-prompted helper only creates or deletes the admin task, then exits.
            if (args.Length > 0 && args[0] == "--task") return Elevated.RunTask(args);

            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } // per-monitor v2
            catch { try { Native.SetProcessDPIAware(); } catch { } }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool uninstall = args.Contains("--uninstall");
            if (!TakeInstance())
            {
                InstanceSignal.Send(uninstall ? "uninstall" : "show");
                return 0;
            }

            // Admin mode: hand over to the elevated copy started by the scheduled task (no UAC prompt).
            var settings = Settings.Load();
            if (!uninstall && settings.SetupDone && settings.ObsAsAdmin && !SystemTweaks.IsElevated && !args.Contains("--no-elevate"))
            {
                ReleaseInstance();
                if (!args.Contains("--startup")) { try { File.WriteAllText(Paths.OpenFlag, "1"); } catch { } }
                if (string.Compare(Paths.CurrentExe, Paths.InstalledExe, StringComparison.OrdinalIgnoreCase) != 0) SystemTweaks.InstallSelf();
                if (SystemTweaks.RunTask())
                {
                    Log.Info("handed over to the elevated task");
                    return 0;
                }
                Log.Warn("admin task didn't start, continuing without admin rights");
                if (!TakeInstance()) { InstanceSignal.Send("show"); return 0; }
            }

            Application.ThreadException += (s, e) => Log.Error("ui thread", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error("unhandled", e.ExceptionObject as Exception);

            if (!WebViewCheck.Ready()) return 1;

            Application.Run(new Engine(args));
            ReleaseInstance();
            return 0;
        }

        static bool TakeInstance()
        {
            try
            {
                bool created;
                instance = new Mutex(true, @"Local\Hindsight.Instance", out created);
                if (!created) { instance.Dispose(); instance = null; }
                return created;
            }
            catch (UnauthorizedAccessException)
            {
                // An elevated copy owns it.
                return false;
            }
        }

        public static void ReleaseInstance()
        {
            try { instance?.ReleaseMutex(); } catch { }
            try { instance?.Dispose(); } catch { }
            instance = null;
            InstanceSignal.Close();
        }
    }

    /// <summary>Lets a second launch (e.g. clicking the Start menu shortcut) poke the running copy, even across admin levels.</summary>
    internal static class InstanceSignal
    {
        const string EventName = @"Local\Hindsight.Signal";
        static string CommandFile => Path.Combine(Paths.Local, "command.txt");
        static EventWaitHandle handle;
        static RegisteredWaitHandle wait;

        public static void Listen(Action<string> onCommand)
        {
            try
            {
                var sec = new EventWaitHandleSecurity();
                var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
                sec.AddAccessRule(new EventWaitHandleAccessRule(everyone, EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify, AccessControlType.Allow));
                bool created;
                handle = new EventWaitHandle(false, EventResetMode.AutoReset, EventName, out created, sec);
                wait = ThreadPool.RegisterWaitForSingleObject(handle, (st, timedOut) =>
                {
                    string cmd = "show";
                    try { if (File.Exists(CommandFile)) { cmd = File.ReadAllText(CommandFile).Trim(); File.Delete(CommandFile); } } catch { }
                    onCommand(cmd);
                }, null, -1, false);
            }
            catch (Exception ex) { Log.Warn("instance signal: " + ex.Message); }
        }

        public static void Send(string cmd)
        {
            try
            {
                Paths.Ensure(Paths.Local);
                File.WriteAllText(CommandFile, cmd);
                using (var h = EventWaitHandle.OpenExisting(EventName, EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify)) h.Set();
            }
            catch (Exception ex) { Log.Warn("signal running copy: " + ex.Message); }
        }

        public static void Close()
        {
            try { wait?.Unregister(null); } catch { }
            try { handle?.Dispose(); } catch { }
            wait = null; handle = null;
        }
    }

    /// <summary>The WebView2 libraries live inside the exe so it ships as a single file.</summary>
    internal static class Embedded
    {
        static readonly Dictionary<string, Assembly> loaded = new Dictionary<string, Assembly>();

        public static Assembly Resolve(object sender, ResolveEventArgs e)
        {
            string name = new AssemblyName(e.Name).Name;
            lock (loaded)
            {
                Assembly a;
                if (loaded.TryGetValue(name, out a)) return a;
                using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("lib." + name + ".dll"))
                {
                    if (st == null) return null;
                    var bytes = new byte[st.Length];
                    st.Read(bytes, 0, bytes.Length);
                    a = Assembly.Load(bytes);
                    loaded[name] = a;
                    return a;
                }
            }
        }

        public static byte[] Bytes(string resource)
        {
            using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            {
                if (st == null) return null;
                var b = new byte[st.Length];
                st.Read(b, 0, b.Length);
                return b;
            }
        }

        public static string Text(string resource)
        {
            var b = Bytes(resource);
            return b == null ? null : System.Text.Encoding.UTF8.GetString(b);
        }

        /// <summary>Writes the native WebView2 loader for this CPU next to our data and returns its folder.</summary>
        public static string ExtractLoader()
        {
            string pa = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "";
            string arch = pa.Equals("ARM64", StringComparison.OrdinalIgnoreCase) ? "arm64" : "x64";
            string dir = Path.Combine(Paths.RuntimeDir, Program.Version, arch);
            string dll = Path.Combine(dir, "WebView2Loader.dll");
            if (!File.Exists(dll))
            {
                Paths.Ensure(dir);
                File.WriteAllBytes(dll, Bytes("lib.WebView2Loader." + arch + ".dll"));
            }
            return dir;
        }
    }

    /// <summary>Hindsight's UI runs on Microsoft Edge WebView2, which ships with Windows 11 and most Windows 10 PCs.</summary>
    internal static class WebViewCheck
    {
        public static bool Ready()
        {
            if (Installed()) return true;
            var r = MessageBox.Show("Hindsight needs Microsoft Edge WebView2, a free component from Microsoft. Install it now?",
                "Hindsight", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
            if (r != DialogResult.OK) return false;
            try
            {
                System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                string file = Path.Combine(Path.GetTempPath(), "MicrosoftEdgeWebview2Setup.exe");
                using (var wc = new System.Net.WebClient()) wc.DownloadFile("https://go.microsoft.com/fwlink/p/?LinkId=2124703", file);
                using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, "/silent /install") { UseShellExecute = true }))
                    p.WaitForExit(10 * 60 * 1000);
            }
            catch (Exception ex)
            {
                Log.Error("webview2 install", ex);
                MessageBox.Show("WebView2 couldn't be installed automatically. Get it from microsoft.com/edge/webview2, then open Hindsight again.", "Hindsight");
                return false;
            }
            return Installed();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool Installed()
        {
            try
            {
                Microsoft.Web.WebView2.Core.CoreWebView2Environment.SetLoaderDllFolderPath(Embedded.ExtractLoader());
                return !string.IsNullOrEmpty(Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString(null));
            }
            catch (Exception ex)
            {
                Log.Info("webview2 not available: " + ex.Message);
                return false;
            }
        }
    }

    /// <summary>Icons drawn from the embedded .ico, with a live dot for the tray.</summary>
    internal static class AppIcon
    {
        static Icon main;
        static Icon trayOn, trayOff;

        public static Icon Main
        {
            get
            {
                if (main == null)
                {
                    var b = Embedded.Bytes("app.ico");
                    main = b != null ? new Icon(new MemoryStream(b)) : SystemIcons.Application;
                }
                return main;
            }
        }

        public static Icon Tray(bool recording)
        {
            if (recording && trayOn != null) return trayOn;
            if (!recording && trayOff != null) return trayOff;
            int size = SystemInformation.SmallIconSize.Width;
            using (var baseIcon = new Icon(Main, size, size))
            using (var bmp = new Bitmap(size, size))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.DrawIcon(baseIcon, new Rectangle(0, 0, size, size));
                    if (!recording)
                    {
                        // Paused: grey it out so it's obvious at a glance.
                        using (var veil = new SolidBrush(Color.FromArgb(150, 40, 38, 52)))
                            g.FillRectangle(veil, 0, 0, size, size);
                    }
                }
                var icon = Icon.FromHandle(bmp.GetHicon());
                if (recording) trayOn = icon; else trayOff = icon;
                return icon;
            }
        }
    }

    internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        static readonly Color Bg = Color.FromArgb(29, 26, 44);
        static readonly Color Hover = Color.FromArgb(46, 41, 66);
        static readonly Color Text1 = Color.FromArgb(236, 233, 245);
        static readonly Color Muted = Color.FromArgb(155, 149, 180);
        static readonly Color Line = Color.FromArgb(52, 47, 72);

        public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text1 : Muted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var r = new Rectangle(Point.Empty, e.Item.Size);
            using (var b = new SolidBrush(e.Item.Selected && e.Item.Enabled ? Hover : Bg)) e.Graphics.FillRectangle(b, r);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var p = new Pen(Line)) e.Graphics.DrawLine(p, 8, y, e.Item.Width - 8, y);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (var p = new Pen(Line)) e.Graphics.DrawRectangle(p, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
        }

        sealed class DarkColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Bg;
            public override Color ImageMarginGradientBegin => Bg;
            public override Color ImageMarginGradientMiddle => Bg;
            public override Color ImageMarginGradientEnd => Bg;
            public override Color MenuBorder => Line;
            public override Color MenuItemBorder => Hover;
            public override Color MenuItemSelected => Hover;
            public override Color SeparatorDark => Line;
            public override Color SeparatorLight => Line;
        }
    }
}
