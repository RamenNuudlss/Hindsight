using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Hindsight
{
    internal enum Status { Setup, Starting, Buffering, Off, Restarting, ObsBusy, ObsClosed, NeedsRestart, Error }

    /// <summary>The whole app: tray icon, OBS lifecycle, hotkeys, clips, setup and removal.</summary>
    internal sealed class Engine : ApplicationContext
    {
        readonly SynchronizationContext ui;
        readonly Settings s;
        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu;
        readonly Hotkeys hotkeys;
        readonly ForegroundTracker foreground;
        readonly ObsSocket obs = new ObsSocket();
        readonly Tape tape = new Tape();
        readonly AppWindow window;
        readonly SemaphoreSlim lifecycle = new SemaphoreSlim(1, 1);
        readonly ConcurrentQueue<KeyValuePair<string, IntPtr>> pendingSaves = new ConcurrentQueue<KeyValuePair<string, IntPtr>>();
        readonly System.Windows.Forms.Timer tapeTimer = new System.Windows.Forms.Timer { Interval = 250 };
        readonly System.Windows.Forms.Timer restartDebounce = new System.Windows.Forms.Timer { Interval = 900 };

        Status status = Status.Starting;
        string statusDetail = "";
        string captureSummary = "";
        Process obsProc;
        volatile bool intentionalStop;
        volatile bool obsSaidExit;
        readonly List<DateTime> crashes = new List<DateTime>();
        bool triedEncoderFallback;
        List<Dictionary<string, object>> micOptions = new List<Dictionary<string, object>>();
        List<Dictionary<string, object>> monitorOptions = new List<Dictionary<string, object>>();
        bool exiting;
        bool launchedForUninstall;

        // "Use my OBS" mode
        bool Mine => s.ObsMode == "mine";
        string mineProfile = "", mineCollection = "", mineSection = "SimpleOutput";
        volatile bool forceLaunchOnce;   // open the person's OBS on the next bring-up even if OpenObsForMe is off
        volatile bool watchingForObs;

        public Engine(string[] args)
        {
            ui = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(ui);
            s = Settings.Load();
            foreground = new ForegroundTracker();
            hotkeys = new Hotkeys();

            menu = BuildMenu();
            tray = new NotifyIcon { Icon = AppIcon.Tray(false), Text = "Hindsight", ContextMenuStrip = menu, Visible = true };
            tray.MouseClick += (o, e) => { if (e.Button == MouseButtons.Left) ToggleWindow(); };

            obs.Event += OnObsEvent;
            obs.Closed += () => Post(() =>
            {
                if (intentionalStop || exiting) return;
                if (status == Status.Buffering || status == Status.Off) SetStatus(Status.Restarting, "Lost contact with OBS…");
                var p = obsProc;
                Task.Delay(4000).ContinueWith(_ =>
                {
                    if (!exiting && !intentionalStop && !obs.Connected && p != null && p == obsProc && ObsProcess.Alive(p.Id)) { var __ = BringUpObs(); }
                });
            });

            tapeTimer.Tick += (o, e) => { if (window.Visible) window.Send("tape", tape.Delta()); };
            tapeTimer.Start();
            restartDebounce.Tick += (o, e) => { restartDebounce.Stop(); var _ = RestartObs("Applying your changes…"); };

            window = new AppWindow(this);
            InstanceSignal.Listen(cmd => ui.Post(_ => OnInstanceCommand(cmd), null));

            if (args.Contains("--uninstall")) { launchedForUninstall = true; ui.Post(_ => OnInstanceCommand("uninstall"), null); return; }

            if (!s.SetupDone)
            {
                SetStatus(Status.Setup, "");
                window.Open("setup");
            }
            else
            {
                bool openFlag = File.Exists(Paths.OpenFlag);
                if (openFlag) { try { File.Delete(Paths.OpenFlag); } catch { } }
                if (s.JustSetUp) { s.JustSetUp = false; s.Save(); window.Open("welcome"); }
                else if (openFlag || !args.Contains("--startup")) window.Open("app");
                // Running a newer download? Make it the installed copy that startup uses.
                try { SystemTweaks.RegisterApp(SystemTweaks.InstallSelf()); } catch (Exception ex) { Log.Warn("self install: " + ex.Message); }
                StartEngine();
            }
        }

        public Settings Current => s;

        // ------------------------------------------------------------------ status

        void SetStatus(Status st, string detail)
        {
            status = st;
            statusDetail = detail ?? "";
            tray.Icon = AppIcon.Tray(st == Status.Buffering);
            string t = "Hindsight — " + StatusLine();
            tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
            UpdateMenu();
            window.Send("status", StatusPayload());
        }

        string StatusLine()
        {
            switch (status)
            {
                case Status.Buffering: return "Keeping the last " + Fmt.Duration(s.ClipSeconds);
                case Status.Off: return "Recording is off";
                case Status.Starting: return "Starting…";
                case Status.Restarting: return "Restarting OBS…";
                case Status.ObsBusy: return "OBS is open with another profile";
                case Status.ObsClosed: return "Paused, OBS is closed";
                case Status.NeedsRestart: return "OBS needs a restart";
                case Status.Error: return "Something needs attention";
                default: return "Not set up yet";
            }
        }

        Dictionary<string, object> StatusPayload()
        {
            return new Dictionary<string, object>
            {
                ["state"] = status.ToString().ToLowerInvariant(),
                ["line"] = StatusLine(),
                ["detail"] = statusDetail,
                ["summary"] = captureSummary,
                ["elevated"] = SystemTweaks.IsElevated,
                ["obsMode"] = s.ObsMode,
                ["profile"] = mineProfile,
                ["collection"] = mineCollection,
            };
        }

        // ------------------------------------------------------------------ tray

        ToolStripMenuItem miStatus, miSave, miRecording, miPause;

        ContextMenuStrip BuildMenu()
        {
            var m = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, Font = new Font("Segoe UI", 9.5f) };
            miStatus = new ToolStripMenuItem("Starting…") { Enabled = false };
            miSave = new ToolStripMenuItem("Save clip", null, (o, e) => SaveClip());
            var miOpen = new ToolStripMenuItem("Open Hindsight", null, (o, e) => window.Open("app")) { Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
            miRecording = new ToolStripMenuItem("Recording", null, (o, e) => SetRecording(!s.Recording));
            var miFolder = new ToolStripMenuItem("Open clips folder", null, (o, e) => OpenFolder(s.EffectiveClipsFolder));
            miPause = new ToolStripMenuItem("Close OBS so I can use it", null, (o, e) => TogglePauseObs());
            var miExit = new ToolStripMenuItem("Quit Hindsight", null, (o, e) => Quit());
            m.Items.AddRange(new ToolStripItem[] { miStatus, new ToolStripSeparator(), miOpen, miSave, miRecording, miFolder, new ToolStripSeparator(), miPause, miExit });
            m.Opening += (o, e) => UpdateMenu();
            return m;
        }

        void UpdateMenu()
        {
            if (miStatus == null) return;
            miStatus.Text = StatusLine();
            miSave.Text = "Save clip   " + s.SaveHotkey;
            miSave.Enabled = status == Status.Buffering;
            miRecording.Text = s.Recording ? "Turn recording off" : "Turn recording on";
            miRecording.Enabled = status == Status.Buffering || status == Status.Off;
            if (Mine)
            {
                miPause.Visible = status == Status.ObsClosed || status == Status.NeedsRestart || status == Status.Error;
                miPause.Text = status == Status.NeedsRestart ? "Restart OBS" : "Open OBS";
            }
            else
            {
                miPause.Visible = true;
                miPause.Text = status == Status.ObsClosed || status == Status.ObsBusy ? "Resume clipping" : "Close OBS so I can use it";
            }
            miPause.Enabled = s.SetupDone;
        }

        void ToggleWindow()
        {
            if (window.Visible && window.ContainsFocus) window.HideWindow();
            else window.Open(s.SetupDone ? "app" : "setup", foreground.LastWindow);
        }

        void OnInstanceCommand(string cmd)
        {
            if (cmd == "uninstall")
            {
                var r = MessageBox.Show("Remove Hindsight from this PC?\n\nYour clips and OBS itself stay. Everything Hindsight changed is put back.",
                    "Remove Hindsight", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (r == DialogResult.OK) { var _ = Remove(); }
                else if (launchedForUninstall) Shutdown();
                return;
            }
            window.Open(s.SetupDone ? "app" : "setup", foreground.LastWindow);
        }

        // ------------------------------------------------------------------ hotkeys

        string hotkeyError = "";

        void RegisterHotkeys()
        {
            hotkeys.Clear();
            var bad = new List<string>();
            if (!hotkeys.Register(s.SaveHotkey, SaveClip)) bad.Add(s.SaveHotkey);
            if (!hotkeys.Register(s.OverlayHotkey, ToggleWindow)) bad.Add(s.OverlayHotkey);
            hotkeyError = bad.Count == 0 ? "" : string.Join(" and ", bad) + (bad.Count == 1 ? " is" : " are") + " already used by another app. Pick a different shortcut.";
            if (bad.Count > 0) Log.Warn(hotkeyError);
        }

        // ------------------------------------------------------------------ OBS lifecycle

        void StartEngine()
        {
            RegisterHotkeys();
            if (hotkeyError.Length > 0) Toast.Show("Shortcut unavailable", hotkeyError, true);
            var _ = BringUpObs();
        }

        async Task BringUpObs()
        {
            await lifecycle.WaitAsync();
            try { await BringUpObsLocked(); }
            catch (Exception ex)
            {
                Log.Error("bring up OBS", ex);
                Post(() => SetStatus(Status.Error, ex.Message));
            }
            finally { lifecycle.Release(); }
        }

        async Task BringUpObsLocked()
        {
            Post(() => SetStatus(Status.Starting, "Starting OBS…"));
            string exe = ObsLocator.Find(s.ObsPath);
            if (exe == null)
            {
                Post(() => SetStatus(Status.Error, "OBS isn't installed anymore. Open Hindsight and run setup again."));
                return;
            }
            s.ObsPath = exe;
            if (Mine) { await BringUpMineLocked(exe); return; }

            var running = ObsProcess.Running();
            if (running != null)
            {
                if (await ConnectObs(4000) && await IsOurProfile())
                {
                    Attach(running);
                    await AfterConnect();
                    return;
                }
                obs.Dispose();
                Post(() => SetStatus(Status.ObsBusy, "OBS is open with your own setup, so clipping is paused."));
                WaitForExitThenResume(running);
                return;
            }

            PrepareObs(exe);
            intentionalStop = false;
            obsSaidExit = false;
            Attach(ObsProcess.Launch(exe));
            if (!await ConnectObs(45000))
            {
                Post(() => SetStatus(Status.Error, "OBS started but Hindsight couldn't connect to it. Make sure no other app is using port " + s.WsPort + "."));
                return;
            }
            await AfterConnect();
        }

        // ------------------------------------------------------------------ "use my OBS" mode

        async Task BringUpMineLocked(string exe)
        {
            ObsFiles.EnsureWebsocket(s);
            s.Save();
            var running = ObsProcess.Running();
            if (running == null)
            {
                if (!s.OpenObsForMe && !forceLaunchOnce)
                {
                    PrepareMineFiles();
                    Post(() => { SetStatus(Status.ObsClosed, "Open OBS and Hindsight starts clipping from it."); WatchForObs(); });
                    return;
                }
                forceLaunchOnce = false;
                PrepareMineFiles();
                intentionalStop = false;
                obsSaidExit = false;
                Attach(ObsProcess.LaunchPlain(exe));
                if (!await ConnectObs(45000))
                {
                    Post(() => SetStatus(Status.Error, "OBS started but Hindsight couldn't connect to it. Make sure no other app is using port " + s.WsPort + "."));
                    return;
                }
            }
            else
            {
                Post(() => SetStatus(Status.Starting, "Connecting to OBS…"));
                if (!await ConnectObs(12000))
                {
                    // Its websocket server was off (Hindsight just turned it on); OBS reads that at launch.
                    obs.Dispose();
                    Post(() => SetStatus(Status.NeedsRestart, "OBS needs one restart so Hindsight can connect to it. Your scenes and projectors come back after."));
                    WaitForExitThenResume(running);
                    return;
                }
                intentionalStop = false;
                obsSaidExit = false;
                Attach(running);
            }
            await ReadMineInfo();
            bool changed = await ConfigureMineReplay();
            Post(() => window.Send("options", Options()));
            if (s.Recording) await StartReplay(changed);
            else Post(() => SetStatus(Status.Off, "Turn recording on to start keeping clips."));
        }

        /// <summary>File-level preparation that has to happen while OBS is closed.</summary>
        void PrepareMineFiles()
        {
            if (ObsProcess.Running() != null) return;
            try
            {
                ObsFiles.RestoreUserPointers();
                ObsFiles.ApplyAppTweaks(true);
                string name = ObsFiles.PrepareUserProfile(s);
                Log.Info("prepared profile " + (name ?? "(none found)"));
            }
            catch (Exception ex) { Log.Warn("prepare OBS files: " + ex.Message); }
        }

        async Task ReadMineInfo()
        {
            var p = await obs.Request("GetProfileList");
            if (p.Ok) mineProfile = p.Data.Str("currentProfileName", "");
            var c = await obs.Request("GetSceneCollectionList");
            if (c.Ok) mineCollection = c.Data.Str("currentSceneCollectionName", "");
            var m = await obs.Request("GetProfileParameter", new Dictionary<string, object> { ["parameterCategory"] = "Output", ["parameterName"] = "Mode" });
            mineSection = m.Ok && m.Data.Str("parameterValue") == "Advanced" ? "AdvOut" : "SimpleOutput";
            captureSummary = MineSummary();
        }

        string MineSummary()
        {
            return "Your OBS: " + (mineProfile.Length > 0 ? mineProfile : "current profile") + (mineCollection.Length > 0 ? ", " + mineCollection : "");
        }

        /// <summary>Sets the replay buffer length in the person's current profile. Returns true if anything changed.</summary>
        async Task<bool> ConfigureMineReplay()
        {
            bool changed = false;
            foreach (var kv in ObsFiles.ReplayValues(s))
            {
                var cur = await obs.Request("GetProfileParameter", new Dictionary<string, object> { ["parameterCategory"] = mineSection, ["parameterName"] = kv.Key });
                if (!cur.Ok) continue;
                string now = cur.Data.Str("parameterValue");
                ObsFiles.Remember(ObsFiles.RememberKey(mineProfile, mineSection, kv.Key), now);
                if (now == kv.Value) continue;
                var r = await obs.Request("SetProfileParameter", new Dictionary<string, object> { ["parameterCategory"] = mineSection, ["parameterName"] = kv.Key, ["parameterValue"] = kv.Value });
                if (!r.Ok) Log.Warn("SetProfileParameter " + kv.Key + ": " + r.Comment);
                changed = true;
            }
            return changed;
        }

        /// <summary>True when the replay buffer was off in this profile before Hindsight turned it on.</summary>
        bool WeEnabledReplay()
        {
            string v;
            return ObsFiles.Recall(ObsFiles.RememberKey(mineProfile, mineSection, "RecRB"), out v) && v != "true";
        }

        void WatchForObs()
        {
            if (watchingForObs) return;
            watchingForObs = true;
            Task.Run(async () =>
            {
                try
                {
                    while (!exiting && Mine && status == Status.ObsClosed)
                    {
                        await Task.Delay(2500);
                        if (ObsProcess.Running() == null) continue;
                        await Task.Delay(3000); // let OBS finish loading
                        if (!exiting && Mine && status == Status.ObsClosed) await BringUpObs();
                        break;
                    }
                }
                finally { watchingForObs = false; }
            });
        }

        /// <summary>Closes and reopens the person's OBS (needed once to switch on its websocket server or replay buffer).</summary>
        async Task RestartUserObs()
        {
            await lifecycle.WaitAsync();
            try
            {
                Post(() => SetStatus(Status.Restarting, "Restarting OBS…"));
                intentionalStop = true;
                obs.Dispose();
                bool closed = await Task.Run(() => ObsProcess.Close());
                obsProc = null;
                if (!closed)
                {
                    intentionalStop = false;
                    Post(() => SetStatus(Status.NeedsRestart, "Hindsight couldn't close OBS (it may be running as administrator). Close OBS yourself and open it again."));
                    var p = ObsProcess.Running();
                    if (p != null) WaitForExitThenResume(p);
                    return;
                }
                forceLaunchOnce = true;
                await BringUpObsLockedSafe();
            }
            finally { lifecycle.Release(); }
        }

        /// <summary>Hindsight's own mode, OBS open with the person's setup: close it and start Hindsight's OBS.</summary>
        async Task TakeOver()
        {
            await lifecycle.WaitAsync();
            try
            {
                Post(() => SetStatus(Status.Restarting, "Closing your OBS…"));
                intentionalStop = true;
                obs.Dispose();
                if (!await Task.Run(() => ObsProcess.Close()))
                {
                    Post(() => SetStatus(Status.ObsBusy, "Hindsight couldn't close OBS. Close it yourself and clipping starts."));
                    return;
                }
                obsProc = null;
                await BringUpObsLockedSafe();
            }
            finally { lifecycle.Release(); }
        }

        async Task SwitchMode()
        {
            await lifecycle.WaitAsync();
            try
            {
                Log.Info("switching OBS mode to " + s.ObsMode);
                Post(() => SetStatus(Status.Restarting, Mine ? "Switching to your OBS…" : "Switching to Hindsight's own OBS…"));
                await StopObsLocked();
                await Task.Run(() =>
                {
                    ObsFiles.RestoreUserPointers();
                    if (!Mine) ObsFiles.RestoreUserProfiles();
                });
                captureSummary = "";
                mineProfile = mineCollection = "";
                forceLaunchOnce = true;
                await BringUpObsLockedSafe();
            }
            finally { lifecycle.Release(); }
            Post(() => { window.Send("options", Options()); window.Send("settings", SettingsPayload()); });
        }

        void PrepareObs(string exe)
        {
            ObsFiles.EnsureWebsocket(s);
            ObsFiles.ApplyAppTweaks(false);
            var enc = Hardware.PickEncoder(s.Encoder);
            string summary;
            ObsFiles.WriteProfile(s, enc, Hardware.ObsVersion(exe), out summary);
            ObsFiles.WriteCollection(s);
            captureSummary = summary;
            s.Save();
        }

        void Attach(Process p)
        {
            obsProc = p;
            if (p == null) return;
            try
            {
                p.EnableRaisingEvents = true;
                p.Exited += (o, e) => Post(() => OnObsExited(p));
            }
            catch (Exception ex)
            {
                // Typically OBS runs as admin and Hindsight doesn't: fall back to polling.
                Log.Info("watching OBS by polling: " + ex.Message);
                int pid = p.Id;
                Task.Run(async () =>
                {
                    while (!exiting && obsProc == p)
                    {
                        await Task.Delay(2000);
                        if (!ObsProcess.Alive(pid)) { Post(() => OnObsExited(p)); return; }
                    }
                });
            }
        }

        async Task<bool> ConnectObs(int withinMs)
        {
            var until = DateTime.UtcNow.AddMilliseconds(withinMs);
            while (DateTime.UtcNow < until)
            {
                if (await obs.ConnectAsync(s.WsPort, s.WsPassword, s.WsAuth, 3000)) return true;
                await Task.Delay(700);
            }
            return false;
        }

        async Task<bool> IsOurProfile()
        {
            var r = await obs.Request("GetProfileList");
            return r.Ok && r.Data.Str("currentProfileName") == Paths.ObsProfile;
        }

        async Task AfterConnect()
        {
            if (string.IsNullOrEmpty(captureSummary))
            {
                var enc = Hardware.PickEncoder(s.Encoder);
                var mon = ObsFiles.MonitorFor(s);
                int w, h;
                Hardware.Size(s.OutputHeight, mon, out w, out h);
                captureSummary = h + "p " + s.Fps + " fps, " + enc.Label + " " + enc.Codec;
            }
            await LoadDeviceLists();
            if (s.Recording) await StartReplay();
            else Post(() => SetStatus(Status.Off, "Turn recording on to start keeping clips."));
        }

        async Task LoadDeviceLists()
        {
            var mics = await obs.Request("GetInputPropertiesListPropertyItems", new Dictionary<string, object> { ["inputName"] = ObsFiles.MicAudio, ["propertyName"] = "device_id" });
            if (mics.Ok)
                micOptions = mics.Data.Arr("propertyItems").OfType<Dictionary<string, object>>()
                    .Select(i => new Dictionary<string, object> { ["id"] = i.Str("itemValue"), ["name"] = i.Str("itemName") }).ToList();
            var mons = await obs.Request("GetInputPropertiesListPropertyItems", new Dictionary<string, object> { ["inputName"] = ObsFiles.ScreenSource, ["propertyName"] = "monitor_id" });
            if (mons.Ok)
                monitorOptions = mons.Data.Arr("propertyItems").OfType<Dictionary<string, object>>()
                    .Select(i => new Dictionary<string, object> { ["id"] = i.Str("itemValue"), ["name"] = i.Str("itemName") })
                    .Where(i => (string)i["id"] != "DUMMY").ToList();
            Post(() => window.Send("options", Options()));
        }

        async Task StartReplay(bool restartIfActive = false)
        {
            if (Mine)
            {
                var pre = await obs.Request("GetReplayBufferStatus");
                if (!pre.Ok)
                {
                    // OBS only creates its replay buffer at launch, so turning it on takes one restart.
                    Log.Info("replay buffer unavailable: " + pre.Comment);
                    Post(() => SetStatus(Status.NeedsRestart, "OBS needs one restart to turn on its replay buffer. Your scenes and projectors come back after."));
                    var p = obsProc;
                    if (p != null) WaitForExitThenResume(p);
                    return;
                }
                if (pre.Data.Bool("outputActive"))
                {
                    if (!restartIfActive)
                    {
                        tape.Started();
                        Post(() => SetStatus(Status.Buffering, ""));
                        return;
                    }
                    intentionalStop = true;
                    await obs.Request("StopReplayBuffer");
                    for (int i = 0; i < 20; i++)
                    {
                        await Task.Delay(250);
                        var st0 = await obs.Request("GetReplayBufferStatus");
                        if (st0.Ok && !st0.Data.Bool("outputActive")) break;
                    }
                    tape.Stopped();
                    intentionalStop = false;
                }
            }
            var r = await obs.Request("StartReplayBuffer", null, 15000);
            if (!r.Ok && r.Code != 500) Log.Warn("StartReplayBuffer: " + r.Code + " " + r.Comment);
            await Task.Delay(1500);
            var st = await obs.Request("GetReplayBufferStatus");
            if (st.Ok && st.Data.Bool("outputActive"))
            {
                tape.Started();
                Post(() => SetStatus(Status.Buffering, ""));
                return;
            }
            if (Mine)
            {
                Post(() => SetStatus(Status.Error, "OBS couldn't start its replay buffer. " + (r.Comment ?? "") + " Check Settings > Output in OBS, then press Try again."));
                return;
            }
            var enc = Hardware.PickEncoder(s.Encoder);
            if (enc.Hardware && !triedEncoderFallback)
            {
                triedEncoderFallback = true;
                Log.Warn("replay buffer didn't start with " + enc.Key + ", falling back to x264");
                s.Encoder = "x264";
                s.Save();
                Post(() => Toast.Show("Switched to CPU encoding", enc.Label + " " + enc.Codec + " didn't start. You can try another encoder in Capture.", true));
                RestartSoon();
                return;
            }
            Post(() => SetStatus(Status.Error, "OBS couldn't start recording. " + (r.Comment ?? "")));
        }

        void RestartSoon()
        {
            Post(() => { restartDebounce.Stop(); restartDebounce.Start(); });
        }

        async Task RestartObs(string why)
        {
            await lifecycle.WaitAsync();
            try
            {
                Post(() => SetStatus(Status.Restarting, why));
                await StopObsLocked();
                await BringUpObsLockedSafe();
            }
            finally { lifecycle.Release(); }
        }

        async Task BringUpObsLockedSafe()
        {
            try { await BringUpObsLocked(); }
            catch (Exception ex) { Log.Error("restart", ex); Post(() => SetStatus(Status.Error, ex.Message)); }
        }

        async Task StopObsLocked()
        {
            intentionalStop = true;
            if (obs.Connected)
            {
                await obs.Request("StopReplayBuffer", null, 5000);
            }
            obs.Dispose();
            await Task.Run(() => ObsProcess.Close());
            obsProc = null;
        }

        void OnObsExited(Process p)
        {
            if (p != obsProc || exiting) return;
            obsProc = null;
            obs.Dispose();
            if (intentionalStop) return;
            if (Mine && obsSaidExit)
            {
                tape.Stopped();
                PrepareMineFiles();
                SetStatus(Status.ObsClosed, "OBS is closed. Clipping starts again when you open it.");
                WatchForObs();
                return;
            }
            if (obsSaidExit)
            {
                ObsFiles.RestoreUserPointers();
                SetStatus(Status.ObsClosed, "OBS was closed, so clipping is paused.");
                return;
            }
            crashes.Add(DateTime.UtcNow);
            crashes.RemoveAll(t => (DateTime.UtcNow - t).TotalMinutes > 3);
            if (crashes.Count >= 3)
            {
                SetStatus(Status.Error, "OBS keeps closing on its own. Check OBS for plugin errors, then press Resume.");
                return;
            }
            SetStatus(Status.Restarting, "OBS closed unexpectedly. Starting it again…");
            forceLaunchOnce = true;
            Task.Delay(2000).ContinueWith(_ => BringUpObs());
        }

        void WaitForExitThenResume(Process p)
        {
            Task.Run(() =>
            {
                try { p.WaitForExit(); }
                catch
                {
                    // No access to an admin OBS from here: poll instead.
                    try { while (!exiting && ObsProcess.Alive(p.Id)) Thread.Sleep(2000); } catch { return; }
                }
                Thread.Sleep(1500);
                if (exiting) return;
                if (Mine)
                {
                    Post(() =>
                    {
                        if (status != Status.NeedsRestart && status != Status.ObsBusy) return;
                        PrepareMineFiles();
                        SetStatus(Status.ObsClosed, "Open OBS again and Hindsight starts clipping.");
                        WatchForObs();
                    });
                    return;
                }
                if (status == Status.ObsBusy) { var _ = BringUpObs(); }
            });
        }

        void TogglePauseObs()
        {
            if (Mine)
            {
                if (status == Status.NeedsRestart) { var _r = RestartUserObs(); }
                else if (status == Status.ObsClosed || status == Status.Error) { forceLaunchOnce = true; var _b = BringUpObs(); }
                return;
            }
            if (status == Status.ObsClosed || status == Status.ObsBusy || status == Status.Error) { var _ = BringUpObs(); return; }
            var __ = PauseObs();
        }

        async Task PauseObs()
        {
            await lifecycle.WaitAsync();
            try
            {
                await StopObsLocked();
                ObsFiles.RestoreUserPointers();
                Post(() => SetStatus(Status.ObsClosed, "OBS is closed so you can use it with your own setup. Resume when you're done."));
            }
            finally { lifecycle.Release(); }
        }

        // ------------------------------------------------------------------ OBS events

        void OnObsEvent(string type, Dictionary<string, object> data)
        {
            switch (type)
            {
                case "InputVolumeMeters":
                    tape.Feed(data);
                    break;
                case "ReplayBufferStateChanged":
                    {
                        bool active = data.Bool("outputActive");
                        string state = data.Str("outputState", "");
                        if (state == "OBS_WEBSOCKET_OUTPUT_STARTED") { tape.Started(); Post(() => SetStatus(Status.Buffering, "")); }
                        else if (state == "OBS_WEBSOCKET_OUTPUT_STOPPED" && !intentionalStop)
                        {
                            tape.Stopped();
                            Post(() => SetStatus(Status.Off, s.Recording ? "Recording stopped in OBS." : "Turn recording on to start keeping clips."));
                        }
                        break;
                    }
                case "ReplayBufferSaved":
                    OnReplaySaved(data.Str("savedReplayPath"));
                    break;
                case "ExitStarted":
                    obsSaidExit = true;
                    break;
                case "CurrentProfileChanged":
                    if (Mine) Task.Run(async () =>
                    {
                        await Task.Delay(800);
                        await ReadMineInfo();
                        bool changed = await ConfigureMineReplay();
                        if (s.Recording) await StartReplay(changed);
                        Post(() => SetStatus(status, statusDetail));
                    });
                    break;
                case "CurrentSceneCollectionChanged":
                    if (Mine)
                    {
                        mineCollection = data.Str("sceneCollectionName", mineCollection);
                        captureSummary = MineSummary();
                        Post(() => SetStatus(status, statusDetail));
                    }
                    break;
            }
        }

        void SaveClip()
        {
            if (status != Status.Buffering)
            {
                Toast.Show("Nothing to save", status == Status.Off ? "Recording is off." : StatusLine(), true, foreground.Current);
                return;
            }
            IntPtr fg = foreground.ForClip(s.ClipSeconds);
            string game = Clips.GameName(fg);
            pendingSaves.Enqueue(new KeyValuePair<string, IntPtr>(game, fg));
            Task.Run(async () =>
            {
                var r = await obs.Request("SaveReplayBuffer");
                if (!r.Ok)
                {
                    KeyValuePair<string, IntPtr> dropped;
                    pendingSaves.TryDequeue(out dropped);
                    Post(() => Toast.Show("Clip not saved", r.Comment ?? "OBS didn't respond.", true, fg));
                }
            });
        }

        void OnReplaySaved(string path)
        {
            KeyValuePair<string, IntPtr> pend;
            if (!pendingSaves.TryDequeue(out pend)) pend = new KeyValuePair<string, IntPtr>(Clips.GameName(foreground.Current), foreground.Current);
            if (string.IsNullOrEmpty(path)) return;
            tape.MarkSave();
            Task.Run(() =>
            {
                string final = Clips.Store(path, pend.Key, s);
                Post(() =>
                {
                    if (s.SavedSound) Chime.Play();
                    if (s.SavedToast) Toast.Show("Clip saved", pend.Key + ", last " + Fmt.Duration(s.ClipSeconds), false, pend.Value);
                    RefreshClips();
                    window.Send("saved", new Dictionary<string, object> { ["path"] = final, ["game"] = pend.Key });
                });
            });
        }

        void SetRecording(bool on)
        {
            s.Recording = on;
            s.Save();
            UpdateMenu();
            if (!obs.Connected) { if (on && (status == Status.Off)) { var _ = BringUpObs(); } return; }
            Task.Run(async () =>
            {
                if (on) await StartReplay();
                else
                {
                    intentionalStop = true;
                    await obs.Request("StopReplayBuffer");
                    tape.Stopped();
                    intentionalStop = false;
                    Post(() => SetStatus(Status.Off, "Turn recording on to start keeping clips."));
                }
            });
        }

        // ------------------------------------------------------------------ settings from the UI

        static readonly HashSet<string> RestartKeys = new HashSet<string> { "ClipSeconds", "Quality", "OutputHeight", "Fps", "Encoder", "ClipsFolder", "SeparateTracks" };

        void ApplySetting(string key, object value, string monitorSize = null)
        {
            var prop = typeof(Settings).GetProperty(key);
            if (prop == null || !prop.CanWrite || key == "SetupDone" || key.StartsWith("Ws")) return;
            object old = prop.GetValue(s, null);
            object val;
            try { val = Convert.ChangeType(value, prop.PropertyType, System.Globalization.CultureInfo.InvariantCulture); }
            catch { return; }
            if (Equals(old, val)) return;

            var canvasBefore = ObsFiles.MonitorFor(s);
            if (key == "MonitorId") s.MonitorSize = monitorSize ?? "";
            prop.SetValue(s, val, null);
            s.Save();
            Log.Info("setting " + key + " = " + val);
            var canvasAfter = ObsFiles.MonitorFor(s);
            bool canvasChanged = canvasBefore.Width != canvasAfter.Width || canvasBefore.Height != canvasAfter.Height;

            switch (key)
            {
                case "Recording": SetRecording((bool)val); break;
                case "SaveHotkey":
                case "OverlayHotkey":
                    RegisterHotkeys();
                    if (hotkeyError.Length > 0)
                    {
                        prop.SetValue(s, old, null);
                        s.Save();
                        RegisterHotkeys();
                        window.Send("error", new Dictionary<string, object> { ["key"] = key, ["message"] = (string)val + " is already used by another app. Pick a different shortcut." });
                    }
                    break;
                case "LaunchAtLogin":
                case "ObsAsAdmin":
                    ApplyStartup(key == "ObsAsAdmin");
                    break;
                case "ObsMode":
                    { var _m = SwitchMode(); }
                    break;
                case "CaptureMode":
                case "MonitorId":
                    if (Mine) break;
                    if (canvasChanged) RestartSoon();
                    else Live(key, val);
                    break;
                case "MonitorSize":
                    if (!Mine && canvasChanged) RestartSoon();
                    break;
                case "DesktopAudio":
                case "Mic":
                case "MicDevice":
                case "NoiseSuppression":
                    Live(key, val);
                    break;
                default:
                    if (Mine && key == "ClipSeconds")
                    {
                        // Only the replay buffer restarts; OBS, scenes and projectors stay up.
                        if (obs.Connected) Task.Run(async () => { await ConfigureMineReplay(); if (s.Recording) await StartReplay(true); });
                    }
                    else if (RestartKeys.Contains(key) && s.SetupDone && !Mine) RestartSoon();
                    break;
            }
            window.Send("settings", SettingsPayload());
        }

        void Live(string key, object val)
        {
            // Never touch sources in the person's own scenes: their "Desktop Audio" is their stream audio too.
            if (!obs.Connected || Mine) return;
            Task.Run(async () =>
            {
                ObsSocket.Reply r = null;
                switch (key)
                {
                    case "CaptureMode":
                        r = await obs.Request("SetCurrentProgramScene", new Dictionary<string, object> { ["sceneName"] = (string)val == "screen" ? ObsFiles.ScreenScene : ObsFiles.GameScene });
                        break;
                    case "MonitorId":
                        r = await obs.Request("SetInputSettings", new Dictionary<string, object> { ["inputName"] = ObsFiles.ScreenSource, ["inputSettings"] = new Dictionary<string, object> { ["monitor_id"] = (string)val }, ["overlay"] = true });
                        break;
                    case "DesktopAudio":
                        r = await obs.Request("SetInputMute", new Dictionary<string, object> { ["inputName"] = ObsFiles.DesktopAudio, ["inputMuted"] = !(bool)val });
                        break;
                    case "Mic":
                        r = await obs.Request("SetInputMute", new Dictionary<string, object> { ["inputName"] = ObsFiles.MicAudio, ["inputMuted"] = !(bool)val });
                        break;
                    case "MicDevice":
                        r = await obs.Request("SetInputSettings", new Dictionary<string, object> { ["inputName"] = ObsFiles.MicAudio, ["inputSettings"] = new Dictionary<string, object> { ["device_id"] = (string)val }, ["overlay"] = true });
                        break;
                    case "NoiseSuppression":
                        r = await obs.Request("SetSourceFilterEnabled", new Dictionary<string, object> { ["sourceName"] = ObsFiles.MicAudio, ["filterName"] = ObsFiles.NoiseFilter, ["filterEnabled"] = (bool)val });
                        break;
                }
                if (r != null && !r.Ok) Log.Warn("live change " + key + " failed: " + r.Comment);
            });
        }

        void ApplyStartup(bool adminChanged)
        {
            string exe = File.Exists(Paths.InstalledExe) ? Paths.InstalledExe : Paths.CurrentExe;
            Task.Run(() =>
            {
                bool ok = ConfigureStartup(exe);
                Post(() =>
                {
                    window.Send("settings", SettingsPayload());
                    if (!ok) { Toast.Show("Admin access declined", "Hindsight will run OBS without administrator rights.", true); return; }
                    if (adminChanged && s.ObsAsAdmin != SystemTweaks.IsElevated)
                        window.Send("restartNeeded", new Dictionary<string, object> { ["admin"] = s.ObsAsAdmin });
                });
            });
        }

        /// <summary>Makes Windows startup match the settings. Returns false if an admin prompt was declined.</summary>
        bool ConfigureStartup(string exe)
        {
            bool ok = true;
            if (s.ObsAsAdmin)
            {
                SystemTweaks.SetRunKey(exe, false);
                if (!SystemTweaks.ConfigureTask(exe, true, s.LaunchAtLogin))
                {
                    ok = false;
                    s.ObsAsAdmin = false;
                    s.Save();
                    SystemTweaks.SetRunKey(exe, s.LaunchAtLogin);
                }
            }
            else
            {
                if (SystemTweaks.TaskExists()) SystemTweaks.ConfigureTask(exe, false, false);
                SystemTweaks.SetRunKey(exe, s.LaunchAtLogin);
            }
            return ok;
        }

        /// <summary>Restarts Hindsight with or without admin rights to match the setting.</summary>
        public void RelaunchForAdminChange()
        {
            Task.Run(async () =>
            {
                await lifecycle.WaitAsync();
                try { await StopObsLocked(); } finally { lifecycle.Release(); }
                Post(() =>
                {
                    exiting = true;
                    Program.ReleaseInstance();
                    string exe = File.Exists(Paths.InstalledExe) ? Paths.InstalledExe : Paths.CurrentExe;
                    if (s.ObsAsAdmin) SystemTweaks.RunTask();
                    else Process.Start("explorer.exe", "\"" + exe + "\""); // explorer starts it without admin rights
                    Shutdown();
                });
            });
        }

        // ------------------------------------------------------------------ setup

        public void RunSetup(bool admin, bool startup, bool mine)
        {
            s.ObsAsAdmin = admin;
            s.LaunchAtLogin = startup;
            s.ObsMode = mine ? "mine" : "hindsight";
            Task.Run(async () =>
            {
                string step = "obs";
                try
                {
                    SetupStep("obs", "active", "Looking for OBS…");
                    string exe = ObsLocator.Find();
                    if (exe == null)
                        exe = ObsLocator.Install((msg, pct) => SetupStep("obs", "active", msg, pct));
                    s.ObsPath = exe;
                    var ver = Hardware.ObsVersion(exe);
                    if (ver != null && ver.Major < 28) throw new Exception("OBS " + ver + " is too old. Update OBS to version 28 or newer, then try again.");
                    SetupStep("obs", "done", "OBS " + (ver != null ? ver.ToString(3) : "") + " found");

                    step = "close";
                    if (mine)
                    {
                        if (ObsProcess.Running() != null)
                        {
                            SetupStep("close", "active", "Connecting to your OBS…");
                            ObsFiles.EnsureWebsocket(s);
                            s.Save();
                            if (await ConnectObs(5000)) { obs.Dispose(); SetupStep("close", "done", "Connected. OBS stays open."); }
                            else
                            {
                                SetupStep("close", "active", "Closing OBS so Hindsight can connect…");
                                if (!ObsProcess.Close()) throw new Exception("Close OBS yourself, then press Try again.");
                                SetupStep("close", "done", "OBS closed. It reopens in a moment.");
                            }
                        }
                        else SetupStep("close", "done", "OBS isn't running");
                    }
                    else
                    {
                        if (ObsProcess.Running() != null)
                        {
                            SetupStep("close", "active", "Closing OBS…");
                            if (!ObsProcess.Close()) throw new Exception("Close OBS yourself, then press Try again.");
                        }
                        SetupStep("close", "done", "OBS is ready to configure");
                    }

                    step = "configure";
                    if (mine)
                    {
                        SetupStep("configure", "active", "Getting your OBS ready for clipping…");
                        ObsFiles.EnsureWebsocket(s);
                        PrepareMineFiles();
                        s.Save();
                        SetupStep("configure", "done", "Uses your own profile, scenes and projectors");
                    }
                    else
                    {
                        SetupStep("configure", "active", "Setting up a Hindsight profile in OBS…");
                        if (string.IsNullOrEmpty(s.Encoder)) s.Encoder = Hardware.Encoders().First().Key;
                        var mon = Hardware.PrimaryMonitor();
                        s.Fps = mon.Hz >= 120 ? 60 : Math.Min(60, Math.Max(30, mon.Hz));
                        s.OutputHeight = mon.Height > 1080 ? 1080 : 0;
                        PrepareObs(exe);
                        var encoder = Hardware.PickEncoder(s.Encoder);
                        SetupStep("configure", "done", encoder.Label + " " + encoder.Codec + ", " + captureSummary.Split(',')[0]);
                    }

                    step = "windows";
                    SetupStep("windows", "active", "Turning off Game Bar capture…");
                    SystemTweaks.DisableGameBarCapture();
                    string installed = SystemTweaks.InstallSelf();
                    SystemTweaks.RegisterApp(installed);
                    if (admin) SetupStep("windows", "active", "Waiting for the admin prompt…");
                    bool startupOk = ConfigureStartup(installed);
                    SetupStep("windows", "done", startupOk ? (s.LaunchAtLogin ? "Starts with Windows" : "Game Bar capture off") : "Running without admin rights");

                    step = "start";
                    s.SetupDone = true;
                    s.Save();
                    if (s.ObsAsAdmin && !SystemTweaks.IsElevated)
                    {
                        SetupStep("start", "active", "Restarting with admin rights…");
                        s.JustSetUp = true;
                        s.Save();
                        await Task.Delay(600);
                        Post(() =>
                        {
                            exiting = true;
                            Program.ReleaseInstance();
                            if (!SystemTweaks.RunTask()) { exiting = false; s.JustSetUp = false; s.Save(); SetupStep("start", "done", "Started"); Post(() => window.Send("setupDone", null)); StartEngine(); return; }
                            Shutdown();
                        });
                        return;
                    }
                    SetupStep("start", "active", mine ? "Starting the replay buffer in your OBS…" : "Starting OBS in the background…");
                    Post(RegisterHotkeys);
                    forceLaunchOnce = true;
                    await BringUpObs();
                    // Status updates arrive on the UI thread; wait for the real outcome (an encoder fallback restarts OBS once).
                    var deadline = DateTime.UtcNow.AddSeconds(90);
                    await Task.Delay(300);
                    while (DateTime.UtcNow < deadline && (status == Status.Starting || status == Status.Restarting)) await Task.Delay(250);
                    if (mine && status == Status.NeedsRestart)
                    {
                        SetupStep("start", "active", "Restarting OBS once to turn on its replay buffer…");
                        await RestartUserObs();
                        await Task.Delay(300);
                        while (DateTime.UtcNow < deadline && (status == Status.Starting || status == Status.Restarting)) await Task.Delay(250);
                    }
                    if (status != Status.Buffering && status != Status.Off) throw new Exception(statusDetail.Length > 0 ? statusDetail : "OBS didn't start recording.");
                    SetupStep("start", "done", "Recording the last " + Fmt.Duration(s.ClipSeconds));
                    Post(() => window.Send("setupDone", null));
                }
                catch (Exception ex)
                {
                    Log.Error("setup failed at " + step, ex);
                    SetupStep(step, "failed", ex.Message);
                }
            });
        }

        void SetupStep(string id, string state, string detail, int progress = -1)
        {
            Post(() => window.Send("setupStep", new Dictionary<string, object> { ["id"] = id, ["state"] = state, ["detail"] = detail, ["progress"] = progress }));
        }

        // ------------------------------------------------------------------ remove

        public async Task Remove()
        {
            window.Send("removing", null);
            exiting = true;
            await lifecycle.WaitAsync();
            bool reopen = Mine && ObsProcess.Running() != null;
            try
            {
                await StopObsLocked();
                await Task.Run(() =>
                {
                    ObsFiles.RestoreUserProfiles();
                    ObsFiles.RestoreUserPointers();
                    ObsFiles.RestoreAppTweaks();
                    ObsFiles.RestoreWebsocket();
                    ObsFiles.DeleteOurObsData();
                    SystemTweaks.RestoreGameBarCapture();
                    string exe = File.Exists(Paths.InstalledExe) ? Paths.InstalledExe : Paths.CurrentExe;
                    SystemTweaks.SetRunKey(exe, false);
                    if (SystemTweaks.TaskExists()) SystemTweaks.ConfigureTask(exe, false, false);
                    SystemTweaks.UnregisterApp();
                    ObsFiles.ClearBackup();
                    try { File.Delete(Paths.SettingsFile); } catch { }
                    try { if (Directory.Exists(Paths.ThumbDir)) Directory.Delete(Paths.ThumbDir, true); } catch { }
                });
            }
            finally { lifecycle.Release(); }
            if (reopen && s.ObsPath != null && File.Exists(s.ObsPath))
            {
                // Their OBS was open before; bring it back with their own settings.
                try { Process.Start(new ProcessStartInfo(s.ObsPath) { WorkingDirectory = Path.GetDirectoryName(s.ObsPath), UseShellExecute = false }); } catch { }
            }
            Log.Info("removed");
            hotkeys.Clear();
            tray.Visible = false;
            window.Send("removed", null);
            await Task.Delay(2500);
            SystemTweaks.ScheduleSelfDelete();
            Shutdown();
        }

        // ------------------------------------------------------------------ quit

        public void Quit()
        {
            if (exiting) return;
            exiting = true;
            tray.Visible = false;
            window.HideWindow();
            Task.Run(async () =>
            {
                await lifecycle.WaitAsync();
                try
                {
                    if (Mine)
                    {
                        // Leave the person's OBS open; only stop the buffer if Hindsight was the one that turned it on.
                        intentionalStop = true;
                        if (obs.Connected && WeEnabledReplay()) await obs.Request("StopReplayBuffer", null, 5000);
                        obs.Dispose();
                    }
                    else
                    {
                        if (obsProc != null || obs.Connected) await StopObsLocked();
                        ObsFiles.RestoreUserPointers();
                    }
                }
                catch (Exception ex) { Log.Error("quit", ex); }
                finally { lifecycle.Release(); }
                Post(Shutdown);
            });
        }

        void Shutdown()
        {
            exiting = true;
            try { tray.Visible = false; tray.Dispose(); } catch { }
            try { hotkeys.Dispose(); foreground.Dispose(); } catch { }
            try { window.Dispose(); } catch { }
            ExitThread();
        }

        // ------------------------------------------------------------------ UI bridge

        public void OnUi(Dictionary<string, object> m)
        {
            string type = m.Str("type", "");
            switch (type)
            {
                case "ready": window.Send("init", InitPayload()); RefreshClips(); break;
                case "set": ApplySetting(m.Str("key"), m.Get("value"), m.Str("size")); break;
                case "saveClip": SaveClip(); break;
                case "recording": SetRecording(m.Bool("on")); break;
                case "refreshClips": RefreshClips(); break;
                case "openClip": OpenPath(m.Str("path")); break;
                case "revealClip": Reveal(m.Str("path")); break;
                case "copyClip": CopyClip(m.Str("path")); break;
                case "deleteClip": DeleteClip(m.Str("path")); break;
                case "openFolder": OpenFolder(s.EffectiveClipsFolder); break;
                case "pickFolder": PickFolder(); break;
                case "showObs": ObsProcess.ShowWindow(); break;
                case "pauseObs": TogglePauseObs(); break;
                case "hotkeyCapture": if (m.Bool("active")) hotkeys.Clear(); else RegisterHotkeys(); break;
                case "setup": RunSetup(m.Bool("admin", true), m.Bool("startup", true), m.Bool("mine")); break;
                case "restartObs": { var _r = RestartUserObs(); } break;
                case "takeOver": { var _t = TakeOver(); } break;
                case "remove": var _ = Remove(); break;
                case "relaunch": RelaunchForAdminChange(); break;
                case "quit": Quit(); break;
                case "openUrl": OpenPath(m.Str("url")); break;
                case "openLog": OpenPath(Paths.LogFile); break;
                case "hide": window.HideWindow(); break;
                case "minimize": window.WindowState = FormWindowState.Minimized; break;
                case "drag": window.DragMove(); break;
                case "doneWelcome": window.Open("app"); break;
            }
        }

        Dictionary<string, object> InitPayload()
        {
            return new Dictionary<string, object>
            {
                ["mode"] = window.Mode,
                ["version"] = Program.Version,
                ["repo"] = Paths.RepoUrl,
                ["status"] = StatusPayload(),
                ["settings"] = SettingsPayload(),
                ["options"] = Options(),
                ["tape"] = tape.Snapshot(s.ClipSeconds),
                ["obsFound"] = ObsLocator.Find(s.ObsPath) != null,
            };
        }

        Dictionary<string, object> SettingsPayload()
        {
            var d = new Dictionary<string, object>();
            foreach (var p in typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (!p.Name.StartsWith("Ws") && p.GetIndexParameters().Length == 0) d[p.Name] = p.GetValue(s, null);
            d["EffectiveClipsFolder"] = s.EffectiveClipsFolder;
            d["hotkeyError"] = hotkeyError;
            return d;
        }

        Dictionary<string, object> Options()
        {
            var primary = Hardware.PrimaryMonitor();
            var canvas = ObsFiles.MonitorFor(s);
            int maxHz = Hardware.Monitors().Select(x => x.Hz).DefaultIfEmpty(60).Max();
            var fps = new List<int> { 30, 60 };
            foreach (var f in new[] { 120, 144, 165, 240 }) if (f <= Math.Max(60, maxHz)) fps.Add(f);
            if (!fps.Contains(s.Fps)) fps.Add(s.Fps);
            fps.Sort();

            var heights = new List<object> { new Dictionary<string, object> { ["value"] = 0, ["label"] = "Same as screen", ["detail"] = canvas.Width + "×" + canvas.Height } };
            foreach (var h in new[] { 1440, 1080, 720 })
                if (h < canvas.Height)
                {
                    int w, hh;
                    Hardware.Size(h, canvas, out w, out hh);
                    heights.Add(new Dictionary<string, object> { ["value"] = h, ["label"] = h + "p", ["detail"] = w + "×" + hh });
                }

            var monitors = monitorOptions.Count > 0
                ? monitorOptions.Cast<object>().ToList()
                : Hardware.Monitors().Select(x => (object)new Dictionary<string, object> { ["id"] = "", ["name"] = x.Name + ": " + x.Width + "x" + x.Height + (x.Primary ? " (primary)" : "") }).ToList();

            return new Dictionary<string, object>
            {
                ["encoders"] = Hardware.Encoders().Select(e => (object)new Dictionary<string, object> { ["key"] = e.Key, ["label"] = e.Label, ["codec"] = e.Codec, ["note"] = e.Note, ["hardware"] = e.Hardware }).ToList(),
                ["fps"] = fps,
                ["heights"] = heights,
                ["mics"] = micOptions.Count > 0 ? micOptions.Cast<object>().ToList() : new List<object> { new Dictionary<string, object> { ["id"] = "default", ["name"] = "Default microphone" } },
                ["monitors"] = monitors,
                ["canvas"] = new Dictionary<string, object> { ["w"] = canvas.Width, ["h"] = canvas.Height },
                ["primary"] = new Dictionary<string, object> { ["w"] = primary.Width, ["h"] = primary.Height, ["hz"] = primary.Hz },
                ["ramMB"] = Hardware.TotalRamBytes() / (1024 * 1024),
                ["gpus"] = Hardware.Gpus,
                ["obsVersion"] = s.ObsPath != null && File.Exists(s.ObsPath) ? (Hardware.ObsVersion(s.ObsPath)?.ToString(3) ?? "") : "",
            };
        }

        void RefreshClips()
        {
            Task.Run(() =>
            {
                var list = Clips.List(s);
                var payload = list.Select(ClipPayload).ToList();
                Post(() => window.Send("clips", new Dictionary<string, object> { ["items"] = payload, ["folder"] = s.EffectiveClipsFolder, ["total"] = list.Sum(c => c.Size) }));
                Shell.Fill(list, changed => Post(() => window.Send("clipsUpdated", new Dictionary<string, object> { ["items"] = changed.Select(ClipPayload).ToList() })));
            });
        }

        object ClipPayload(ClipInfo c)
        {
            string rel = c.Path.StartsWith(s.EffectiveClipsFolder, StringComparison.OrdinalIgnoreCase)
                ? c.Path.Substring(s.EffectiveClipsFolder.Length).TrimStart('\\') : null;
            return new Dictionary<string, object>
            {
                ["path"] = c.Path,
                ["name"] = Path.GetFileNameWithoutExtension(c.Path),
                ["ext"] = Path.GetExtension(c.Path).TrimStart('.').ToLowerInvariant(),
                ["game"] = c.Game,
                ["size"] = c.Size,
                ["saved"] = (long)(c.Saved.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalMilliseconds,
                ["seconds"] = Math.Round(c.Seconds, 1),
                ["thumb"] = File.Exists(Path.Combine(Paths.ThumbDir, c.Thumb)) ? "https://thumbs.hindsight/" + c.Thumb : null,
                ["src"] = rel == null ? null : "https://clips.hindsight/" + string.Join("/", rel.Split('\\').Select(Uri.EscapeDataString)),
            };
        }

        void OpenPath(string p)
        {
            if (string.IsNullOrEmpty(p)) return;
            if (!p.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !File.Exists(p) && !Directory.Exists(p)) return;
            try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); } catch (Exception ex) { Log.Warn("open: " + ex.Message); }
        }

        void Reveal(string p)
        {
            if (!File.Exists(p)) return;
            try { Process.Start("explorer.exe", "/select,\"" + p + "\""); } catch { }
        }

        void OpenFolder(string dir)
        {
            Paths.Ensure(dir);
            try { Process.Start("explorer.exe", "\"" + dir + "\""); } catch { }
        }

        void CopyClip(string p)
        {
            if (!File.Exists(p)) return;
            try
            {
                var files = new System.Collections.Specialized.StringCollection { p };
                Clipboard.SetFileDropList(files);
                window.Send("copied", new Dictionary<string, object> { ["path"] = p });
            }
            catch (Exception ex) { Log.Warn("copy: " + ex.Message); }
        }

        void DeleteClip(string p)
        {
            if (!File.Exists(p) || !p.StartsWith(s.EffectiveClipsFolder, StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(p, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                string dir = Path.GetDirectoryName(p);
                if (!string.Equals(dir.TrimEnd('\\'), s.EffectiveClipsFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (Exception ex) { Log.Warn("delete: " + ex.Message); }
            RefreshClips();
        }

        void PickFolder()
        {
            string chosen = FolderPicker.Pick(window.Handle, s.EffectiveClipsFolder);
            if (chosen == null) return;
            ApplySetting("ClipsFolder", chosen);
            window.MapClipsFolder();
            RefreshClips();
        }

        void Post(Action a)
        {
            ui.Post(_ => { try { a(); } catch (Exception ex) { Log.Error("ui action", ex); } }, null);
        }
    }

    /// <summary>
    /// Keeps a low-resolution history of audio levels (4 per second) so the UI can draw the
    /// buffer as a strip of everything that's currently in memory.
    /// </summary>
    internal sealed class Tape
    {
        const int PerSecond = 4;
        const int Capacity = 20 * 60 * PerSecond + 64;
        readonly float[] ring = new float[Capacity];
        long written;          // total buckets ever written
        long sentUpTo;         // buckets already pushed to the UI
        long startedAt = -1;   // bucket index when the buffer last started
        readonly List<long> saves = new List<long>();
        float bucketPeak = -1;
        DateTime bucketStart = DateTime.UtcNow;
        bool running;
        readonly object gate = new object();

        public void Started() { lock (gate) { if (!running) { running = true; startedAt = written; } } }
        public void Stopped() { lock (gate) { running = false; startedAt = -1; } }
        public void MarkSave() { lock (gate) { saves.Add(written); if (saves.Count > 50) saves.RemoveAt(0); } }

        public void Feed(Dictionary<string, object> data)
        {
            float peak = 0;
            foreach (var o in data.Arr("inputs").OfType<Dictionary<string, object>>())
            {
                foreach (var ch in o.Arr("inputLevelsMul"))
                {
                    var arr = ch as System.Collections.IList; // object[] or ArrayList depending on the serializer path
                    if (arr == null || arr.Count < 2) continue;
                    float mul = Convert.ToSingle(arr[1], System.Globalization.CultureInfo.InvariantCulture);
                    if (mul > peak) peak = mul;
                }
            }
            lock (gate)
            {
                Advance();
                if (peak > bucketPeak) bucketPeak = peak;
            }
        }

        void Advance()
        {
            var now = DateTime.UtcNow;
            while ((now - bucketStart).TotalMilliseconds >= 1000.0 / PerSecond)
            {
                float v = running ? Norm(Math.Max(0, bucketPeak)) : -1;
                ring[written % Capacity] = v;
                written++;
                bucketPeak = -1;
                bucketStart = bucketStart.AddMilliseconds(1000.0 / PerSecond);
                if ((now - bucketStart).TotalSeconds > 5) bucketStart = now; // after sleep or a stall
            }
        }

        static float Norm(float mul)
        {
            if (mul <= 0.000001f) return 0;
            double db = 20 * Math.Log10(mul);
            return (float)Math.Max(0, Math.Min(1, (db + 60) / 60));
        }

        public Dictionary<string, object> Snapshot(int seconds)
        {
            lock (gate)
            {
                Advance();
                int n = (int)Math.Min(written, Math.Min(Capacity, seconds * PerSecond));
                var vals = new List<double>(n);
                for (long i = written - n; i < written; i++) vals.Add(Math.Round(ring[i % Capacity], 2));
                sentUpTo = written;
                return new Dictionary<string, object>
                {
                    ["perSecond"] = PerSecond,
                    ["values"] = vals,
                    ["bufferedSeconds"] = startedAt < 0 ? 0 : (written - startedAt) / (double)PerSecond,
                    ["saves"] = saves.Where(x => x > written - n).Select(x => (written - x) / (double)PerSecond).ToList(),
                };
            }
        }

        public Dictionary<string, object> Delta()
        {
            lock (gate)
            {
                Advance();
                var vals = new List<double>();
                if (written - sentUpTo > Capacity) sentUpTo = written - Capacity;
                for (long i = sentUpTo; i < written; i++) vals.Add(Math.Round(ring[i % Capacity], 2));
                sentUpTo = written;
                return new Dictionary<string, object>
                {
                    ["values"] = vals,
                    ["bufferedSeconds"] = startedAt < 0 ? 0 : (written - startedAt) / (double)PerSecond,
                    ["saves"] = saves.Select(x => (written - x) / (double)PerSecond).Where(x => x < 1200).ToList(),
                };
            }
        }
    }
}
