using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Hindsight
{
    /// <summary>
    /// The Hindsight window: a borderless frame around the HTML interface. It's created once at startup
    /// and kept warm, so the overlay shortcut opens it instantly.
    /// </summary>
    internal sealed class AppWindow : Form
    {
        readonly Engine engine;
        readonly WebView2 web;
        bool ready;
        bool allowClose;
        string mode = "app";

        public string Mode => mode;

        public AppWindow(Engine engine)
        {
            this.engine = engine;
            Text = "Hindsight";
            Icon = AppIcon.Main;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = true;
            BackColor = Color.FromArgb(21, 18, 31);
            KeyPreview = true;
            web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
            Controls.Add(web);
            CreateHandle();
            web.CreateControl();
            Init();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= Native.CS_DROPSHADOW;
                cp.Style |= 0x00020000; // WS_MINIMIZEBOX so the taskbar button can minimize it
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.RoundCorners(Handle);
        }

        async void Init()
        {
            try
            {
                WriteUi();
                var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--disable-features=msSmartScreenProtection --autoplay-policy=no-user-gesture-required" };
                var env = await CoreWebView2Environment.CreateAsync(null, Paths.WebViewData, options);
                await web.EnsureCoreWebView2Async(env);
                var cw = web.CoreWebView2;
                cw.Settings.AreDefaultContextMenusEnabled = false;
                cw.Settings.AreDevToolsEnabled = File.Exists(Path.Combine(Paths.Local, "devtools"));
                cw.Settings.IsStatusBarEnabled = false;
                cw.Settings.IsZoomControlEnabled = false;
                cw.Settings.AreBrowserAcceleratorKeysEnabled = false;
                cw.Settings.IsPasswordAutosaveEnabled = false;
                cw.Settings.IsGeneralAutofillEnabled = false;
                try { cw.Settings.IsNonClientRegionSupportEnabled = true; } catch { } // CSS app-region: drag, with snap layouts
                cw.SetVirtualHostNameToFolderMapping("app.hindsight", Paths.UiDir, CoreWebView2HostResourceAccessKind.Allow);
                Paths.Ensure(Paths.ThumbDir);
                cw.SetVirtualHostNameToFolderMapping("thumbs.hindsight", Paths.ThumbDir, CoreWebView2HostResourceAccessKind.Allow);
                // Clips are served by Hindsight itself with byte-range support, so big files stream and seek.
                cw.AddWebResourceRequestedFilter("https://clips.hindsight/*", CoreWebView2WebResourceContext.All);
                cw.WebResourceRequested += OnClipRequest;
                cw.WebMessageReceived += OnMessage;
                cw.NewWindowRequested += (s, e) =>
                {
                    e.Handled = true;
                    engine.OnUi(new Dictionary<string, object> { ["type"] = "openUrl", ["url"] = e.Uri });
                };
                cw.NavigationStarting += (s, e) =>
                {
                    if (!e.Uri.StartsWith("https://app.hindsight/", StringComparison.OrdinalIgnoreCase))
                    {
                        e.Cancel = true;
                        engine.OnUi(new Dictionary<string, object> { ["type"] = "openUrl", ["url"] = e.Uri });
                    }
                };
                cw.Navigate("https://app.hindsight/index.html");
            }
            catch (Exception ex)
            {
                Log.Error("webview init", ex);
                MessageBox.Show("Hindsight's window couldn't start: " + ex.Message, "Hindsight", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        static void WriteUi()
        {
            Paths.Ensure(Paths.UiDir);
            foreach (var f in new[] { "index.html", "bricolage.woff2", "icon.png" })
            {
                var bytes = Embedded.Bytes("ui." + f);
                if (bytes != null) File.WriteAllBytes(Path.Combine(Paths.UiDir, f), bytes);
            }
        }

        /// <summary>Kept for callers; clips are looked up in the current folder on every request.</summary>
        public void MapClipsFolder() { }

        void OnClipRequest(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            try
            {
                string root = Path.GetFullPath(engine.Current.EffectiveClipsFolder).TrimEnd('\\') + "\\";
                var uri = new Uri(e.Request.Uri);
                string rel = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')).Replace('/', '\\');
                string path = Path.GetFullPath(Path.Combine(root, rel));
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                {
                    e.Response = web.CoreWebView2.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
                    return;
                }
                var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                long total = fs.Length, start = 0, end = total - 1;
                string range = null;
                try { range = e.Request.Headers.GetHeader("Range"); } catch { }
                bool partial = false;
                if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes="))
                {
                    var parts = range.Substring(6).Split(',')[0].Split('-');
                    long a, b;
                    if (parts[0].Length > 0 && long.TryParse(parts[0], out a)) start = a;
                    else if (parts.Length > 1 && long.TryParse(parts[1], out b)) start = Math.Max(0, total - b); // suffix range
                    if (parts[0].Length > 0 && parts.Length > 1 && long.TryParse(parts[1], out b)) end = Math.Min(b, total - 1);
                    partial = true;
                }
                if (start >= total) { fs.Dispose(); e.Response = web.CoreWebView2.Environment.CreateWebResourceResponse(null, 416, "Range Not Satisfiable", "Content-Range: bytes */" + total); return; }
                string ext = Path.GetExtension(path).ToLowerInvariant();
                string type = ext == ".mkv" ? "video/x-matroska" : ext == ".mov" ? "video/quicktime" : "video/mp4";
                long len = end - start + 1;
                string headers = "Content-Type: " + type + "\r\nAccept-Ranges: bytes\r\nContent-Length: " + len +
                                 (partial ? "\r\nContent-Range: bytes " + start + "-" + end + "/" + total : "") + "\r\nCache-Control: no-store";
                e.Response = web.CoreWebView2.Environment.CreateWebResourceResponse(new SliceStream(fs, start, len), partial ? 206 : 200, partial ? "Partial Content" : "OK", headers);
            }
            catch (Exception ex)
            {
                Log.Warn("serve clip: " + ex.Message);
                try { e.Response = web.CoreWebView2.Environment.CreateWebResourceResponse(null, 500, "Error", ""); } catch { }
            }
        }

        /// <summary>A read-only window onto part of a file, for range responses.</summary>
        sealed class SliceStream : Stream
        {
            readonly Stream inner; readonly long start, length; long pos;
            public SliceStream(Stream inner, long start, long length) { this.inner = inner; this.start = start; this.length = length; inner.Position = start; }
            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => length;
            public override long Position { get { return pos; } set { pos = Math.Max(0, Math.Min(length, value)); inner.Position = start + pos; } }
            public override int Read(byte[] buffer, int offset, int count)
            {
                long left = length - pos;
                if (left <= 0) return 0;
                int n = inner.Read(buffer, offset, (int)Math.Min(count, left));
                pos += n;
                return n;
            }
            public override long Seek(long offset, SeekOrigin origin)
            {
                Position = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? pos + offset : length + offset;
                return pos;
            }
            public override void Flush() { }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        }

        void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            Dictionary<string, object> m;
            try { m = Json.Parse(e.TryGetWebMessageAsString()); }
            catch { return; }
            if (m.Str("type") == "ready") ready = true;
            engine.OnUi(m);
        }

        /// <summary>Pushes a message to the page. Dropped until the page has loaded; it asks for a full snapshot when it does.</summary>
        public void Send(string type, object data)
        {
            if (!ready || IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(() => Send(type, data))); return; }
            try { web.CoreWebView2?.PostWebMessageAsJson(Json.Write(new Dictionary<string, object> { ["type"] = type, ["data"] = data })); }
            catch (Exception ex) { Log.Warn("send to page: " + ex.Message); }
        }

        public void Open(string newMode, IntPtr near = default(IntPtr))
        {
            bool modeChanged = newMode != mode;
            mode = newMode;
            if (modeChanged || !Visible) Place(near);
            Send("mode", new Dictionary<string, object> { ["mode"] = mode });
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            TopMost = true;
            Activate();
            Native.SetForegroundWindow(Handle);
            TopMost = false;
            web.Focus();
            Send("shown", null);
        }

        void Place(IntPtr near)
        {
            var screen = near != IntPtr.Zero ? Screen.FromHandle(near) : Screen.FromPoint(Cursor.Position);
            float scale = 1f;
            try
            {
                IntPtr mon = Native.MonitorFromWindow(near != IntPtr.Zero ? near : Handle, 2);
                uint x, y;
                if (Native.GetDpiForMonitor(mon, 0, out x, out y) == 0 && x > 0) scale = x / 96f;
            }
            catch { }
            var wa = screen.WorkingArea;
            bool small = mode == "setup";
            int w = (int)((small ? 620 : 1120) * scale);
            int h = (int)((small ? 720 : 728) * scale);
            w = Math.Min(w, (int)(wa.Width * 0.94));
            h = Math.Min(h, (int)(wa.Height * 0.94));
            Bounds = new Rectangle(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2, w, h);
        }

        public void HideWindow()
        {
            if (!Visible) return;
            Send("hidden", null);
            Hide();
        }

        public void DragMove()
        {
            Native.ReleaseCapture();
            Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HTCAPTION, IntPtr.Zero);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!allowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                if (mode == "setup" && !engine.Current.SetupDone) engine.Quit();
                else HideWindow();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            allowClose = true;
            if (disposing) { try { web.Dispose(); } catch { } }
            base.Dispose(disposing);
        }
    }

    /// <summary>The modern Windows folder picker (IFileOpenDialog in folder mode).</summary>
    internal static class FolderPicker
    {
        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        class FileOpenDialogRCW { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint c, IntPtr specs);
            void SetFileTypeIndex(uint i);
            void GetFileTypeIndex(out uint i);
            void Advise(IntPtr sink, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint fos);
            void GetOptions(out uint fos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem psi);
            void GetCurrentSelection(out IShellItem psi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem psi);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attribs);
            void Compare(IShellItem psi, uint hint, out int order);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr pbc, ref Guid riid, out IShellItem item);

        public static string Pick(IntPtr owner, string start)
        {
            try
            {
                var dlg = (IFileDialog)new FileOpenDialogRCW();
                dlg.SetOptions(0x20 /* PICKFOLDERS */ | 0x40 /* FORCEFILESYSTEM */ | 0x800 /* PATHMUSTEXIST */);
                dlg.SetTitle("Choose where clips are saved");
                dlg.SetOkButtonLabel("Save clips here");
                if (Directory.Exists(start))
                {
                    var iid = typeof(IShellItem).GUID;
                    IShellItem item;
                    SHCreateItemFromParsingName(start, IntPtr.Zero, ref iid, out item);
                    dlg.SetFolder(item);
                }
                if (dlg.Show(owner) != 0) return null;
                IShellItem result;
                dlg.GetResult(out result);
                string path;
                result.GetDisplayName(0x80058000 /* SIGDN_FILESYSPATH */, out path);
                return path;
            }
            catch (Exception ex)
            {
                Log.Warn("folder picker: " + ex.Message);
                using (var f = new FolderBrowserDialog { SelectedPath = start, Description = "Choose where clips are saved" })
                    return f.ShowDialog() == DialogResult.OK ? f.SelectedPath : null;
            }
        }
    }
}
