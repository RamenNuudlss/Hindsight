using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32;

namespace Hindsight
{
    /// <summary>Finds an existing OBS install, or installs it.</summary>
    internal static class ObsLocator
    {
        public static string Find(string hint = null)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(hint)) candidates.Add(hint);
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var k = hk.OpenSubKey(@"SOFTWARE\OBS Studio"))
                    {
                        var dir = k?.GetValue(null) as string;
                        if (!string.IsNullOrEmpty(dir)) candidates.Add(Path.Combine(dir, "bin", "64bit", "obs64.exe"));
                    }
                }
                catch { }
            }
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            candidates.Add(Path.Combine(pf, "obs-studio", "bin", "64bit", "obs64.exe"));
            candidates.Add(@"C:\Program Files\obs-studio\bin\64bit\obs64.exe");
            // Steam copy of OBS
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    var steam = k?.GetValue("SteamPath") as string;
                    if (!string.IsNullOrEmpty(steam))
                        candidates.Add(Path.Combine(steam.Replace('/', '\\'), "steamapps", "common", "OBS Studio", "bin", "64bit", "obs64.exe"));
                }
            }
            catch { }
            return candidates.FirstOrDefault(File.Exists);
        }

        /// <summary>Installs OBS with winget, or falls back to the official installer from GitHub.</summary>
        public static string Install(Action<string, int> progress)
        {
            progress("Installing OBS with winget…", -1);
            try
            {
                var psi = new ProcessStartInfo("winget",
                    "install --id OBSProject.OBSStudio -e --silent --accept-package-agreements --accept-source-agreements --disable-interactivity")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    p.OutputDataReceived += (s, e) => { };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(15 * 60 * 1000)) { try { p.Kill(); } catch { } }
                    Log.Info("winget exit " + (p.HasExited ? p.ExitCode.ToString() : "timeout"));
                }
                var found = Find();
                if (found != null) return found;
            }
            catch (Exception ex) { Log.Warn("winget unavailable: " + ex.Message); }

            progress("Downloading OBS…", 0);
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            string api;
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "Hindsight";
                api = wc.DownloadString("https://api.github.com/repos/obsproject/obs-studio/releases/latest");
            }
            var rel = Json.Parse(api);
            string url = null, name = null;
            foreach (var a in rel.Arr("assets").OfType<Dictionary<string, object>>())
            {
                string n = a.Str("name", "");
                if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && n.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0
                    && n.IndexOf("arm64", StringComparison.OrdinalIgnoreCase) < 0 && n.IndexOf("Installer", StringComparison.OrdinalIgnoreCase) >= 0)
                { url = a.Str("browser_download_url"); name = n; break; }
            }
            if (url == null) throw new Exception("Couldn't find the OBS installer on GitHub.");

            string file = Path.Combine(Path.GetTempPath(), name);
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "Hindsight";
                var done = new ManualResetEvent(false);
                Exception err = null;
                wc.DownloadProgressChanged += (s, e) => progress("Downloading OBS…", e.ProgressPercentage);
                wc.DownloadFileCompleted += (s, e) => { err = e.Error; done.Set(); };
                wc.DownloadFileAsync(new Uri(url), file);
                done.WaitOne();
                if (err != null) throw new Exception("OBS download failed: " + err.Message);
            }

            progress("Installing OBS…", -1);
            var install = new ProcessStartInfo(file, "/S") { UseShellExecute = true, Verb = "runas" };
            using (var p = Process.Start(install)) p.WaitForExit(15 * 60 * 1000);
            try { File.Delete(file); } catch { }
            var path = Find();
            if (path == null) throw new Exception("OBS didn't finish installing.");
            return path;
        }
    }

    /// <summary>
    /// Writes Hindsight's own OBS profile and scene collection, and remembers every
    /// value it changes outside of them so Remove can put it back.
    /// </summary>
    internal static class ObsFiles
    {
        // ---------- restore record ----------

        static Dictionary<string, object> LoadBackup()
        {
            try { if (File.Exists(Paths.BackupFile)) return Json.Parse(File.ReadAllText(Paths.BackupFile)); }
            catch (Exception ex) { Log.Error("restore file unreadable", ex); }
            return new Dictionary<string, object>();
        }

        static void SaveBackup(Dictionary<string, object> b)
        {
            Paths.Ensure(Paths.Local);
            File.WriteAllText(Paths.BackupFile, Json.Write(b));
        }

        /// <summary>Remember the original value of something the first time we change it.</summary>
        public static void Remember(string key, string originalValue)
        {
            var b = LoadBackup();
            if (b.ContainsKey(key)) return;
            b[key] = originalValue;
            SaveBackup(b);
        }

        public static void Forget(string key)
        {
            var b = LoadBackup();
            if (b.Remove(key)) SaveBackup(b);
        }

        public static bool Recall(string key, out string value)
        {
            var b = LoadBackup();
            object v;
            if (b.TryGetValue(key, out v)) { value = v as string; return true; }
            value = null;
            return false;
        }

        public static void ClearBackup() { try { File.Delete(Paths.BackupFile); } catch { } }

        // ---------- OBS app-level settings ----------

        static IEnumerable<string> GlobalInis()
        {
            // OBS 31+ keeps user settings in user.ini; older versions use global.ini. Edit whichever exist.
            string g = Path.Combine(Paths.ObsConfig, "global.ini");
            string u = Path.Combine(Paths.ObsConfig, "user.ini");
            bool any = false;
            if (File.Exists(u)) { any = true; yield return u; }
            if (File.Exists(g)) { any = true; yield return g; }
            if (!any) { yield return u; yield return g; }
        }

        // { section, key, value, "mine" when it only applies to "use my OBS" mode }
        static readonly string[][] AppTweaks =
        {
            new[] { "General", "ConfirmOnExit", "false", "" },
            new[] { "General", "FirstRun", "true", "" },             // skip OBS's first-launch auto-configuration wizard
            new[] { "BasicWindow", "SysTrayEnabled", "true", "" },     // needed for --minimize-to-tray
            new[] { "BasicWindow", "SaveProjectors", "true", "mine" }, // projectors come back after OBS restarts
        };

        /// <summary>Only call while OBS is closed: OBS rewrites these files when it exits.</summary>
        public static void ApplyAppTweaks(bool mine)
        {
            foreach (var path in GlobalInis())
            {
                var ini = new IniFile(path);
                string tag = Path.GetFileName(path);
                foreach (var t in AppTweaks)
                {
                    if (t[3] == "mine" && !mine) continue;
                    Remember("ini|" + tag + "|" + t[0] + "|" + t[1], ini.Get(t[0], t[1]));
                    ini.Set(t[0], t[1], t[2]);
                }
                ini.Save();
            }
        }

        public static void RestoreAppTweaks()
        {
            foreach (var path in GlobalInis())
            {
                if (!File.Exists(path)) continue;
                var ini = new IniFile(path);
                string tag = Path.GetFileName(path);
                foreach (var t in AppTweaks)
                {
                    string v;
                    if (Recall("ini|" + tag + "|" + t[0] + "|" + t[1], out v)) ini.SetOrRemove(t[0], t[1], v);
                }
                ini.Save();
            }
        }

        // ---------- the person's own profile ("use my OBS" mode) ----------

        /// <summary>Replay buffer values Hindsight needs in whichever profile it clips from.</summary>
        public static Dictionary<string, string> ReplayValues(Settings s)
        {
            long ramMb = Hardware.TotalRamBytes() / (1024 * 1024);
            // A generous cap: OBS only uses memory as the buffer fills, so this just stops it cutting clips short.
            int cap = (int)Math.Max(256, Math.Min(ramMb / 3, Hardware.BufferMegabytes(60000, s.ClipSeconds)));
            return new Dictionary<string, string>
            {
                ["RecRB"] = "true",
                ["RecRBTime"] = s.ClipSeconds.ToString(CultureInfo.InvariantCulture),
                ["RecRBSize"] = cap.ToString(CultureInfo.InvariantCulture),
            };
        }

        public static string RememberKey(string profile, string section, string key)
        {
            return "mine|" + profile + "|" + section + "|" + key;
        }

        static string ProfilesRoot => Path.Combine(Paths.ObsConfig, "basic", "profiles");

        /// <summary>The folder of the profile OBS will open with next (the last one used).</summary>
        public static string CurrentUserProfileDir()
        {
            foreach (var path in GlobalInis())
            {
                if (!File.Exists(path)) continue;
                var ini = new IniFile(path);
                string dir = ini.Get("Basic", "ProfileDir");
                if (!string.IsNullOrEmpty(dir) && dir != Paths.ObsProfile && Directory.Exists(Path.Combine(ProfilesRoot, dir))) return Path.Combine(ProfilesRoot, dir);
                string name = ini.Get("Basic", "Profile");
                if (!string.IsNullOrEmpty(name) && name != Paths.ObsProfile)
                {
                    string byName = ProfileDirByName(name);
                    if (byName != null) return byName;
                }
            }
            if (!Directory.Exists(ProfilesRoot)) return null;
            return Directory.GetDirectories(ProfilesRoot).FirstOrDefault(d => Path.GetFileName(d) != Paths.ObsProfile && File.Exists(Path.Combine(d, "basic.ini")));
        }

        static string ProfileDirByName(string name)
        {
            if (!Directory.Exists(ProfilesRoot)) return null;
            foreach (var d in Directory.GetDirectories(ProfilesRoot))
            {
                string ini = Path.Combine(d, "basic.ini");
                if (!File.Exists(ini)) continue;
                string n = new IniFile(ini).Get("General", "Name") ?? Path.GetFileName(d);
                if (n == name) return d;
            }
            return null;
        }

        /// <summary>
        /// Turns on the replay buffer in the person's profile file before OBS starts, so OBS creates it at launch.
        /// Remembers the original values for Remove. Only call while OBS is closed.
        /// </summary>
        public static string PrepareUserProfile(Settings s)
        {
            string dir = CurrentUserProfileDir();
            if (dir == null) return null;
            var ini = new IniFile(Path.Combine(dir, "basic.ini"));
            string name = ini.Get("General", "Name") ?? Path.GetFileName(dir);
            string section = ini.Get("Output", "Mode") == "Advanced" ? "AdvOut" : "SimpleOutput";
            foreach (var kv in ReplayValues(s))
            {
                Remember(RememberKey(name, section, kv.Key), ini.Get(section, kv.Key));
                ini.Set(section, kv.Key, kv.Value);
            }
            ini.Save();
            return name;
        }

        /// <summary>Puts the person's replay buffer settings back. Only call while OBS is closed.</summary>
        public static void RestoreUserProfiles()
        {
            var b = LoadBackup();
            var keys = b.Keys.Where(k => k.StartsWith("mine|")).ToList();
            foreach (var group in keys.GroupBy(k => k.Split('|')[1]))
            {
                string dir = ProfileDirByName(group.Key);
                if (dir != null)
                {
                    var ini = new IniFile(Path.Combine(dir, "basic.ini"));
                    foreach (var k in group)
                    {
                        var parts = k.Split('|');
                        ini.SetOrRemove(parts[2], parts[3], b[k] as string);
                    }
                    ini.Save();
                }
                foreach (var k in group) b.Remove(k);
            }
            SaveBackup(b);
        }

        static readonly string[] PointerKeys = { "Profile", "ProfileDir", "SceneCollection", "SceneCollectionFile" };

        /// <summary>
        /// Launching OBS with our profile makes it the "last used" one. Save whatever the
        /// person had before, so their own OBS opens to their own setup afterwards.
        /// </summary>
        public static void SaveUserPointers()
        {
            foreach (var path in GlobalInis())
            {
                if (!File.Exists(path)) continue;
                var ini = new IniFile(path);
                string profile = ini.Get("Basic", "Profile");
                if (profile == Paths.ObsProfile) continue; // already pointing at us
                var b = LoadBackup();
                foreach (var k in PointerKeys) b["ptr|" + Path.GetFileName(path) + "|" + k] = ini.Get("Basic", k);
                SaveBackup(b);
            }
        }

        public static void RestoreUserPointers()
        {
            foreach (var path in GlobalInis())
            {
                if (!File.Exists(path)) continue;
                var ini = new IniFile(path);
                if (ini.Get("Basic", "Profile") != Paths.ObsProfile) continue;
                bool any = false;
                foreach (var k in PointerKeys)
                {
                    string v;
                    if (Recall("ptr|" + Path.GetFileName(path) + "|" + k, out v)) { ini.SetOrRemove("Basic", k, v); any = true; }
                }
                if (any) ini.Save();
            }
        }

        // ---------- obs-websocket ----------

        /// <summary>
        /// Turns on OBS's built-in websocket server so Hindsight can control OBS. If the person
        /// already uses it (Streamer.bot etc.), their port and password are reused untouched.
        /// </summary>
        public static void EnsureWebsocket(Settings s)
        {
            string path = Paths.ObsWebsocketConfig;
            string original = File.Exists(path) ? File.ReadAllText(path) : null;
            Remember("websocket", original);
            var cfg = Json.Parse(original);
            if (cfg.Bool("server_enabled") && cfg.Int("server_port") > 0)
            {
                s.WsPort = cfg.Int("server_port", 4455);
                s.WsAuth = cfg.Bool("auth_required", true);
                s.WsPassword = cfg.Str("server_password", "");
                if (s.WsAuth && string.IsNullOrEmpty(s.WsPassword))
                {
                    s.WsPassword = RandomPassword();
                    cfg["server_password"] = s.WsPassword;
                }
                else return;
            }
            else
            {
                if (string.IsNullOrEmpty(s.WsPassword)) s.WsPassword = RandomPassword();
                s.WsPort = cfg.Int("server_port", 4455);
                if (s.WsPort <= 0) s.WsPort = 4455;
                s.WsAuth = true;
                cfg["server_enabled"] = true;
                cfg["server_port"] = s.WsPort;
                cfg["auth_required"] = true;
                cfg["server_password"] = s.WsPassword;
                cfg["alerts_enabled"] = false;
                cfg["first_load"] = false;
            }
            Paths.Ensure(Path.GetDirectoryName(path));
            File.WriteAllText(path, Json.Write(cfg));
        }

        public static void RestoreWebsocket()
        {
            string v;
            if (!Recall("websocket", out v)) return;
            try
            {
                if (v == null) File.Delete(Paths.ObsWebsocketConfig);
                else File.WriteAllText(Paths.ObsWebsocketConfig, v);
            }
            catch (Exception ex) { Log.Warn("websocket restore: " + ex.Message); }
        }

        static string RandomPassword()
        {
            var bytes = new byte[18];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes).Replace('+', 'x').Replace('/', 'y').TrimEnd('=');
        }

        // ---------- profile ----------

        public static void WriteProfile(Settings s, EncoderOption enc, Version obsVersion, out string summary)
        {
            var mon = MonitorFor(s);
            int ow, oh;
            Hardware.Size(s.OutputHeight, mon, out ow, out oh);
            int kbps = Hardware.Bitrate(s.Quality, ow, oh, s.Fps, enc.Codec);
            int rbMb = Hardware.BufferMegabytes(kbps, s.ClipSeconds);
            string folder = s.EffectiveClipsFolder;
            Paths.Ensure(folder);

            var ini = IniFile.Empty(Path.Combine(Paths.ObsProfileDir, "basic.ini"));
            ini.Set("General", "Name", Paths.ObsProfile);

            ini.Set("Output", "Mode", "Advanced");
            ini.Set("Output", "FilenameFormatting", "%CCYY-%MM-%DD %hh-%mm-%ss");
            ini.Set("Output", "DelayEnable", "false");

            ini.Set("AdvOut", "RecType", "Standard");
            ini.Set("AdvOut", "RecFilePath", folder);
            ini.Set("AdvOut", "RecFormat", "mp4");
            ini.Set("AdvOut", "RecFormat2", "mp4");
            ini.Set("AdvOut", "RecEncoder", enc.ObsId(obsVersion));
            ini.Set("AdvOut", "RecUseRescale", "false");
            ini.Set("AdvOut", "RecTracks", s.SeparateTracks ? "7" : "1");
            ini.Set("AdvOut", "RecRB", "true");
            ini.Set("AdvOut", "RecRBTime", s.ClipSeconds.ToString(CultureInfo.InvariantCulture));
            ini.Set("AdvOut", "RecRBSize", rbMb.ToString(CultureInfo.InvariantCulture));
            ini.Set("AdvOut", "RecRBPrefix", "Replay");
            ini.Set("AdvOut", "RecSplitFile", "false");
            ini.Set("AdvOut", "Track1Bitrate", "192");
            ini.Set("AdvOut", "Track2Bitrate", "160");
            ini.Set("AdvOut", "Track3Bitrate", "160");
            ini.Set("AdvOut", "Track1Name", s.SeparateTracks ? "Everything" : "");
            ini.Set("AdvOut", "Track2Name", s.SeparateTracks ? "Game and desktop" : "");
            ini.Set("AdvOut", "Track3Name", s.SeparateTracks ? "Microphone" : "");

            ini.Set("Video", "BaseCX", mon.Width.ToString(CultureInfo.InvariantCulture));
            ini.Set("Video", "BaseCY", mon.Height.ToString(CultureInfo.InvariantCulture));
            ini.Set("Video", "OutputCX", ow.ToString(CultureInfo.InvariantCulture));
            ini.Set("Video", "OutputCY", oh.ToString(CultureInfo.InvariantCulture));
            ini.Set("Video", "FPSType", "1");
            ini.Set("Video", "FPSInt", s.Fps.ToString(CultureInfo.InvariantCulture));
            ini.Set("Video", "ScaleType", "lanczos");
            ini.Set("Video", "ColorFormat", "NV12");
            ini.Set("Video", "ColorSpace", "709");
            ini.Set("Video", "ColorRange", "Partial");

            ini.Set("Audio", "SampleRate", "48000");
            ini.Set("Audio", "ChannelSetup", "Stereo");
            ini.Save();

            File.WriteAllText(Path.Combine(Paths.ObsProfileDir, "recordEncoder.json"), Json.Write(EncoderSettings(enc, kbps)));
            summary = oh + "p " + s.Fps + " fps, " + enc.Label + " " + enc.Codec + ", " + (kbps / 1000) + " Mbps";
        }

        static Dictionary<string, object> EncoderSettings(EncoderOption enc, int kbps)
        {
            var d = new Dictionary<string, object>
            {
                ["rate_control"] = "CBR",
                ["bitrate"] = kbps,
                ["keyint_sec"] = 2,
            };
            switch (enc.Vendor)
            {
                case "nvidia":
                    d["preset"] = "p5"; d["preset2"] = "p5"; d["tune"] = "hq"; d["multipass"] = "qres";
                    d["profile"] = enc.Codec == "H.264" ? "high" : "main"; d["bf"] = 2; d["psycho_aq"] = true; d["lookahead"] = false;
                    break;
                case "amd":
                    d["preset"] = "quality"; d["profile"] = enc.Codec == "H.264" ? "high" : "main";
                    break;
                case "intel":
                    d["target_usage"] = "TU4"; d["profile"] = enc.Codec == "H.264" ? "high" : "main";
                    break;
                default:
                    d["preset"] = "veryfast"; d["profile"] = "high"; d["tune"] = "";
                    break;
            }
            return d;
        }

        public static MonitorInfo MonitorFor(Settings s)
        {
            // The canvas follows the display being captured; game capture uses the primary display.
            var all = Hardware.Monitors();
            if (s.CaptureMode == "screen" && !string.IsNullOrEmpty(s.MonitorSize))
            {
                var parts = s.MonitorSize.Split('x');
                int w, h;
                if (parts.Length == 2 && int.TryParse(parts[0], out w) && int.TryParse(parts[1], out h) && w > 0 && h > 0)
                    return all.FirstOrDefault(m => m.Width == w && m.Height == h) ?? new MonitorInfo { Width = w, Height = h, Hz = 60, Index = 0 };
            }
            return all.FirstOrDefault(m => m.Primary) ?? Hardware.PrimaryMonitor();
        }

        // ---------- scene collection ----------

        public const string GameScene = "Game capture";
        public const string ScreenScene = "Screen capture";
        public const string GameSource = "Game";
        public const string ScreenSource = "Screen";
        public const string DesktopAudio = "Desktop Audio";
        public const string MicAudio = "Microphone";
        public const string NoiseFilter = "Noise suppression";

        public static void WriteCollection(Settings s)
        {
            var mon = MonitorFor(s);
            const long prevVer = 503316480; // OBS 30.0.0
            string gameId = Guid.NewGuid().ToString(), screenId = Guid.NewGuid().ToString();
            string gameSceneId = Guid.NewGuid().ToString(), screenSceneId = Guid.NewGuid().ToString();

            var gameCapture = Source("game_capture", GameSource, gameId, new Dictionary<string, object>
            {
                ["capture_mode"] = "any_fullscreen",
                ["capture_cursor"] = false,
                ["allow_transparency"] = false,
                ["capture_overlays"] = false,
                ["anti_cheat_hook"] = true,
                ["limit_framerate"] = false,
                ["priority"] = 2,
                ["hook_rate"] = 1,
            }, 0, prevVer);

            var screenSettings = new Dictionary<string, object> { ["capture_cursor"] = true, ["method"] = 0 };
            if (!string.IsNullOrEmpty(s.MonitorId)) screenSettings["monitor_id"] = s.MonitorId;
            else screenSettings["monitor"] = mon.Index;
            var screenCapture = Source("monitor_capture", ScreenSource, screenId, screenSettings, 0, prevVer);

            var gameScene = Scene(GameScene, gameSceneId, GameSource, gameId, mon, prevVer);
            var screenScene = Scene(ScreenScene, screenSceneId, ScreenSource, screenId, mon, prevVer);

            int deskMixers = s.SeparateTracks ? (1 | 2) : 1;
            int micMixers = s.SeparateTracks ? (1 | 4) : 1;

            var desktop = Source("wasapi_output_capture", DesktopAudio, Guid.NewGuid().ToString(),
                new Dictionary<string, object> { ["device_id"] = "default" }, deskMixers, prevVer);
            desktop["muted"] = !s.DesktopAudio;

            var mic = Source("wasapi_input_capture", MicAudio, Guid.NewGuid().ToString(),
                new Dictionary<string, object> { ["device_id"] = string.IsNullOrEmpty(s.MicDevice) ? "default" : s.MicDevice }, micMixers, prevVer);
            mic["muted"] = !s.Mic;
            mic["filters"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["id"] = "noise_suppress_filter_v2",
                    ["versioned_id"] = "noise_suppress_filter_v2",
                    ["name"] = NoiseFilter,
                    ["uuid"] = Guid.NewGuid().ToString(),
                    ["enabled"] = s.NoiseSuppression,
                    ["settings"] = new Dictionary<string, object> { ["method"] = "rnnoise" },
                    ["prev_ver"] = prevVer,
                    ["mixers"] = 0, ["sync"] = 0, ["flags"] = 0, ["volume"] = 1.0, ["balance"] = 0.5, ["muted"] = false,
                    ["private_settings"] = new Dictionary<string, object>(),
                }
            };

            string current = s.CaptureMode == "screen" ? ScreenScene : GameScene;
            var doc = new Dictionary<string, object>
            {
                ["name"] = Paths.ObsCollection,
                ["current_scene"] = current,
                ["current_program_scene"] = current,
                ["scene_order"] = new List<object>
                {
                    new Dictionary<string, object> { ["name"] = GameScene },
                    new Dictionary<string, object> { ["name"] = ScreenScene },
                },
                ["sources"] = new List<object> { gameScene, screenScene, gameCapture, screenCapture },
                ["groups"] = new List<object>(),
                ["DesktopAudioDevice1"] = desktop,
                ["AuxAudioDevice1"] = mic,
                ["current_transition"] = "Cut",
                ["transition_duration"] = 0,
                ["transitions"] = new List<object>(),
                ["quick_transitions"] = new List<object>(),
                ["saved_projectors"] = new List<object>(),
                ["preview_locked"] = false,
                ["scaling_enabled"] = false,
                ["modules"] = new Dictionary<string, object>(),
            };
            Paths.Ensure(Path.GetDirectoryName(Paths.ObsCollectionFile));
            File.WriteAllText(Paths.ObsCollectionFile, Json.Write(doc));
        }

        static Dictionary<string, object> Source(string id, string name, string uuid, Dictionary<string, object> settings, int mixers, long prevVer)
        {
            return new Dictionary<string, object>
            {
                ["prev_ver"] = prevVer,
                ["name"] = name,
                ["uuid"] = uuid,
                ["id"] = id,
                ["versioned_id"] = id,
                ["settings"] = settings,
                ["mixers"] = mixers,
                ["sync"] = 0,
                ["flags"] = 0,
                ["volume"] = 1.0,
                ["balance"] = 0.5,
                ["enabled"] = true,
                ["muted"] = false,
                ["push-to-mute"] = false,
                ["push-to-talk"] = false,
                ["hotkeys"] = new Dictionary<string, object>(),
                ["deinterlace_mode"] = 0,
                ["deinterlace_field_order"] = 0,
                ["monitoring_type"] = 0,
                ["private_settings"] = new Dictionary<string, object>(),
            };
        }

        static Dictionary<string, object> Scene(string name, string uuid, string itemName, string itemUuid, MonitorInfo mon, long prevVer)
        {
            var item = new Dictionary<string, object>
            {
                ["name"] = itemName,
                ["source_uuid"] = itemUuid,
                ["visible"] = true,
                ["locked"] = true,
                ["rot"] = 0.0,
                ["pos"] = new Dictionary<string, object> { ["x"] = 0.0, ["y"] = 0.0 },
                ["scale"] = new Dictionary<string, object> { ["x"] = 1.0, ["y"] = 1.0 },
                ["align"] = 5,
                ["bounds_type"] = 2,
                ["bounds_align"] = 0,
                ["bounds_crop"] = false,
                ["bounds"] = new Dictionary<string, object> { ["x"] = (double)mon.Width, ["y"] = (double)mon.Height },
                ["crop_left"] = 0, ["crop_top"] = 0, ["crop_right"] = 0, ["crop_bottom"] = 0,
                ["id"] = 1,
                ["group_item_backup"] = false,
                ["scale_filter"] = "disable",
                ["blend_method"] = "default",
                ["blend_type"] = "normal",
                ["private_settings"] = new Dictionary<string, object>(),
            };
            var src = Source("scene", name, uuid, new Dictionary<string, object>
            {
                ["id_counter"] = 1,
                ["custom_size"] = false,
                ["items"] = new List<object> { item },
            }, 0, prevVer);
            return src;
        }

        public static void DeleteOurObsData()
        {
            try { if (Directory.Exists(Paths.ObsProfileDir)) Directory.Delete(Paths.ObsProfileDir, true); } catch (Exception ex) { Log.Warn("profile delete: " + ex.Message); }
            try
            {
                if (File.Exists(Paths.ObsCollectionFile)) File.Delete(Paths.ObsCollectionFile);
                string bak = Paths.ObsCollectionFile + ".bak";
                if (File.Exists(bak)) File.Delete(bak);
            }
            catch (Exception ex) { Log.Warn("collection delete: " + ex.Message); }
        }
    }
}
