using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Hindsight
{
    public sealed class ClipInfo
    {
        public string Path;
        public string Game;
        public long Size;
        public DateTime Saved;
        public double Seconds;
        public string Thumb; // file name inside the thumbs folder, served to the UI
    }

    /// <summary>Names, files and finds clips.</summary>
    internal static class Clips
    {
        static readonly string[] GenericProducts =
        {
            "java", "unreal", "unity", "windows", "electron", "chrome", "chromium", "microsoft", "steam",
            "operating system", "launcher", "shipping", "directx", "vulkan", "game", "application",
        };

        static readonly Dictionary<string, string> KnownExe = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["explorer"] = "Desktop",
            ["javaw"] = "Minecraft",
            ["Minecraft.Windows"] = "Minecraft",
            ["cs2"] = "Counter-Strike 2",
            ["r5apex"] = "Apex Legends",
            ["r5apex_dx12"] = "Apex Legends",
            ["FortniteClient-Win64-Shipping"] = "Fortnite",
            ["VALORANT-Win64-Shipping"] = "VALORANT",
            ["RobloxPlayerBeta"] = "Roblox",
            ["EscapeFromTarkov"] = "Escape from Tarkov",
            ["RocketLeague"] = "Rocket League",
            ["League of Legends"] = "League of Legends",
            ["overwatch"] = "Overwatch",
            ["cod"] = "Call of Duty",
            ["GTA5"] = "Grand Theft Auto V",
            ["GTA5_Enhanced"] = "Grand Theft Auto V",
            ["dota2"] = "Dota 2",
            ["RainbowSix"] = "Rainbow Six Siege",
            ["RainbowSix_Vulkan"] = "Rainbow Six Siege",
            ["Marvel-Win64-Shipping"] = "Marvel Rivals",
            ["deadlock"] = "Deadlock",
        };

        /// <summary>Works out a folder-friendly game name from whatever window had focus.</summary>
        public static string GameName(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return "Desktop";
                uint pid;
                Native.GetWindowThreadProcessId(hwnd, out pid);
                string exe = Native.ProcessPath(pid);
                string title = Native.WindowTitle(hwnd).Trim();
                if (string.IsNullOrEmpty(exe)) return Clean(string.IsNullOrEmpty(title) ? "Desktop" : title);

                string stem = System.IO.Path.GetFileNameWithoutExtension(exe);
                string known;
                if (KnownExe.TryGetValue(stem, out known)) return known;

                var fv = FileVersionInfo.GetVersionInfo(exe);
                string product = (fv.ProductName ?? "").Trim();
                if (Usable(product)) return Clean(product);
                if (title.Length > 0 && title.Length <= 48) return Clean(Regex.Replace(title, @"\s+v?\d+(\.\d+)+.*$", ""));
                string desc = (fv.FileDescription ?? "").Trim();
                if (Usable(desc)) return Clean(desc);
                stem = Regex.Replace(stem, @"(-Win64-Shipping|-Win64|_x64|_dx1[12]|_vulkan|_be|_eac|64)$", "", RegexOptions.IgnoreCase);
                return Clean(stem);
            }
            catch (Exception ex)
            {
                Log.Warn("game name: " + ex.Message);
                return "Desktop";
            }
        }

        static bool Usable(string s)
        {
            if (string.IsNullOrWhiteSpace(s) || s.Length > 48) return false;
            string l = s.ToLowerInvariant();
            return !GenericProducts.Any(g => l.Contains(g));
        }

        static string Clean(string s)
        {
            s = s.Replace("™", "").Replace("®", "").Replace("©", "");
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c.ToString(), "");
            s = Regex.Replace(s, @"\s+", " ").Trim().TrimEnd('.');
            if (s.Length > 60) s = s.Substring(0, 60).Trim();
            return s.Length == 0 ? "Desktop" : s;
        }

        /// <summary>Moves a fresh replay into the clips library, named after the game.</summary>
        public static string Store(string savedPath, string game, Settings s)
        {
            string root = s.EffectiveClipsFolder;
            string dir = s.SortByGame ? System.IO.Path.Combine(root, game) : root;
            Paths.Ensure(dir);
            string stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
            string ext = System.IO.Path.GetExtension(savedPath);
            string target = System.IO.Path.Combine(dir, game + " " + stamp + ext);
            int n = 2;
            while (System.IO.File.Exists(target)) target = System.IO.Path.Combine(dir, game + " " + stamp + " (" + n++ + ")" + ext);

            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    System.IO.File.Move(savedPath, target);
                    return target;
                }
                catch (IOException) { Thread.Sleep(250); }
                catch (UnauthorizedAccessException) { Thread.Sleep(250); }
            }
            Log.Warn("couldn't move clip, leaving it where OBS put it: " + savedPath);
            return savedPath;
        }

        public static List<ClipInfo> List(Settings s, int max = 240)
        {
            string root = s.EffectiveClipsFolder;
            var result = new List<ClipInfo>();
            if (!Directory.Exists(root)) return result;
            IEnumerable<FileInfo> files;
            try
            {
                files = new DirectoryInfo(root).EnumerateFiles("*.*", SearchOption.AllDirectories)
                    .Where(f => f.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.LastWriteTimeUtc).Take(max).ToList();
            }
            catch (Exception ex) { Log.Warn("clip scan: " + ex.Message); return result; }

            foreach (var f in files)
            {
                string game = string.Equals(f.Directory.FullName.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                    ? GuessGameFromName(f.Name) : f.Directory.Name;
                result.Add(new ClipInfo
                {
                    Path = f.FullName,
                    Game = game,
                    Size = f.Length,
                    Saved = f.LastWriteTime,
                    Thumb = ThumbName(f),
                    Seconds = Shell.CachedDuration(f),
                });
            }
            return result;
        }

        static string GuessGameFromName(string name)
        {
            var m = Regex.Match(name, @"^(.*?) \d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}");
            return m.Success && m.Groups[1].Value != "Replay" ? m.Groups[1].Value : "Unsorted";
        }

        public static string ThumbName(FileInfo f)
        {
            using (var md5 = MD5.Create())
            {
                var h = md5.ComputeHash(Encoding.UTF8.GetBytes(f.FullName.ToLowerInvariant() + "|" + f.Length + "|" + f.LastWriteTimeUtc.Ticks));
                return BitConverter.ToString(h, 0, 8).Replace("-", "").ToLowerInvariant() + ".jpg";
            }
        }
    }

    /// <summary>
    /// Thumbnails and durations come from Windows' own shell (the same thumbnails Explorer shows),
    /// worked on a dedicated STA thread so the UI never waits.
    /// </summary>
    internal static class Shell
    {
        static readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();
        static readonly ConcurrentDictionary<string, double> durations = new ConcurrentDictionary<string, double>();
        static Thread worker;

        static void Start()
        {
            if (worker != null) return;
            worker = new Thread(() => { foreach (var a in queue.GetConsumingEnumerable()) { try { a(); } catch (Exception ex) { Log.Warn("shell: " + ex.Message); } } })
            { IsBackground = true, Name = "thumbnails" };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }

        public static double CachedDuration(FileInfo f)
        {
            double d;
            return durations.TryGetValue(f.FullName + "|" + f.Length, out d) ? d : 0;
        }

        /// <summary>Makes any missing thumbnails, then calls back (on the worker thread) with the clips that changed.</summary>
        public static void Fill(List<ClipInfo> clips, Action<List<ClipInfo>> done)
        {
            Start();
            var copy = clips.ToList();
            queue.Add(() =>
            {
                Paths.Ensure(Paths.ThumbDir);
                var changed = new List<ClipInfo>();
                foreach (var c in copy)
                {
                    bool touched = false;
                    string thumbPath = Path.Combine(Paths.ThumbDir, c.Thumb);
                    if (!System.IO.File.Exists(thumbPath) && System.IO.File.Exists(c.Path))
                    {
                        if (MakeThumb(c.Path, thumbPath)) touched = true;
                    }
                    if (c.Seconds <= 0)
                    {
                        double secs = Duration(c.Path);
                        if (secs > 0)
                        {
                            durations[c.Path + "|" + c.Size] = secs;
                            c.Seconds = secs;
                            touched = true;
                        }
                    }
                    if (touched) changed.Add(c);
                    if (changed.Count >= 12) { done(changed); changed = new List<ClipInfo>(); }
                }
                if (changed.Count > 0) done(changed);
                PruneThumbs(copy);
            });
        }

        static void PruneThumbs(List<ClipInfo> live)
        {
            try
            {
                var keep = new HashSet<string>(live.Select(c => c.Thumb), StringComparer.OrdinalIgnoreCase);
                var files = new DirectoryInfo(Paths.ThumbDir).GetFiles("*.jpg");
                if (files.Length < 400) return;
                foreach (var f in files.Where(f => !keep.Contains(f.Name)).OrderBy(f => f.LastWriteTimeUtc).Take(files.Length - 300)) f.Delete();
            }
            catch { }
        }

        // --- COM interop ---

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage([In] SIZE size, [In] int flags, [Out] out IntPtr phbm);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SIZE { public int cx, cy; }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct PROPERTYKEY { public Guid fmtid; public uint pid; }

        [ComImport, Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem2
        {
            // IShellItem
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IntPtr ppsi);
            void GetDisplayName(uint sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IntPtr psi, uint hint, out int piOrder);
            // IShellItem2
            void GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
            void GetPropertyStoreWithCreateObject(int flags, IntPtr punkCreateObject, ref Guid riid, out IntPtr ppv);
            void GetPropertyStoreForKeys(IntPtr rgKeys, uint cKeys, int flags, ref Guid riid, out IntPtr ppv);
            void GetPropertyDescriptionList(ref PROPERTYKEY keyType, ref Guid riid, out IntPtr ppv);
            void Update(IntPtr pbc);
            void GetProperty(ref PROPERTYKEY key, IntPtr ppropvar);
            void GetCLSID(ref PROPERTYKEY key, out Guid pclsid);
            void GetFileTime(ref PROPERTYKEY key, out long pft);
            void GetInt32(ref PROPERTYKEY key, out int pi);
            void GetString(ref PROPERTYKEY key, [MarshalAs(UnmanagedType.LPWStr)] out string ppsz);
            void GetUInt32(ref PROPERTYKEY key, out uint pui);
            void GetUInt64(ref PROPERTYKEY key, out ulong pull);
            void GetBool(ref PROPERTYKEY key, out int pf);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr h);

        static bool MakeThumb(string video, string jpg)
        {
            object item = null;
            IntPtr hbmp = IntPtr.Zero;
            try
            {
                var iid = typeof(IShellItemImageFactory).GUID;
                SHCreateItemFromParsingName(video, IntPtr.Zero, ref iid, out item);
                var factory = (IShellItemImageFactory)item;
                // SIIGBF_BIGGERSIZEOK (0x1) | SIIGBF_THUMBNAILONLY (0x8)
                int hr = factory.GetImage(new SIZE { cx = 480, cy = 270 }, 0x1 | 0x8, out hbmp);
                if (hr != 0 || hbmp == IntPtr.Zero) return false;
                using (var bmp = Image.FromHbitmap(hbmp))
                using (var outBmp = Fit(bmp, 480, 270))
                {
                    var codec = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
                    var ep = new EncoderParameters(1);
                    ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 82L);
                    outBmp.Save(jpg, codec, ep);
                }
                return true;
            }
            catch (Exception ex) { Log.Info("thumbnail failed for " + Path.GetFileName(video) + ": " + ex.Message); return false; }
            finally
            {
                if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
                if (item != null) Marshal.ReleaseComObject(item);
            }
        }

        static Bitmap Fit(Image src, int w, int h)
        {
            var b = new Bitmap(w, h);
            using (var g = Graphics.FromImage(b))
            {
                g.Clear(Color.FromArgb(21, 18, 31));
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                double scale = Math.Max(w / (double)src.Width, h / (double)src.Height); // cover
                int dw = (int)(src.Width * scale), dh = (int)(src.Height * scale);
                g.DrawImage(src, (w - dw) / 2, (h - dh) / 2, dw, dh);
            }
            return b;
        }

        static double Duration(string video)
        {
            object item = null;
            try
            {
                var iid = typeof(IShellItem2).GUID;
                SHCreateItemFromParsingName(video, IntPtr.Zero, ref iid, out item);
                var si = (IShellItem2)item;
                // PKEY_Media_Duration {64440490-4C8B-11D1-8B70-080036B11A03}, 3 — in 100ns units
                var key = new PROPERTYKEY { fmtid = new Guid("64440490-4C8B-11D1-8B70-080036B11A03"), pid = 3 };
                ulong v;
                si.GetUInt64(ref key, out v);
                return v / 10000000.0;
            }
            catch { return 0; }
            finally { if (item != null) Marshal.ReleaseComObject(item); }
        }
    }
}
