using System;
using System.IO;

namespace Hindsight
{
    /// <summary>Everything the user can change. Saved as JSON in %LOCALAPPDATA%\Hindsight.</summary>
    public sealed class Settings
    {
        public bool SetupDone { get; set; }
        public bool JustSetUp { get; set; }
        public string ObsPath { get; set; }

        // "hindsight" = Hindsight runs its own background OBS with its own profile.
        // "mine" = clip from the person's own OBS: their profile, scenes and projectors stay as they are.
        public string ObsMode { get; set; } = "hindsight";
        public bool OpenObsForMe { get; set; } = true;      // "mine" mode: open OBS when Hindsight starts

        // Recording
        public bool Recording { get; set; } = true;
        public int ClipSeconds { get; set; } = 120;
        public string Quality { get; set; } = "high";      // low | medium | high | max
        public int OutputHeight { get; set; } = 0;         // 0 = same as the screen
        public int Fps { get; set; } = 60;
        public string Encoder { get; set; } = "";          // encoder key, "" = pick automatically
        public string CaptureMode { get; set; } = "game";  // game | screen
        public string MonitorId { get; set; } = "";       // OBS monitor_id, "" = primary display
        public string MonitorSize { get; set; } = "";     // "2560x1440" of the chosen display

        // Audio
        public bool DesktopAudio { get; set; } = true;
        public bool Mic { get; set; } = true;
        public string MicDevice { get; set; } = "default";
        public bool NoiseSuppression { get; set; } = true;
        public bool SeparateTracks { get; set; } = false;

        // Hotkeys & feedback
        public string SaveHotkey { get; set; } = "Alt+F10";
        public string OverlayHotkey { get; set; } = "Alt+X";
        public bool SavedSound { get; set; } = true;
        public bool SavedToast { get; set; } = true;

        // Library
        public string ClipsFolder { get; set; } = "";
        public bool SortByGame { get; set; } = true;

        // Startup
        public bool LaunchAtLogin { get; set; } = true;
        public bool ObsAsAdmin { get; set; } = true;

        // obs-websocket connection (local only)
        public int WsPort { get; set; } = 4455;
        public string WsPassword { get; set; } = "";
        public bool WsAuth { get; set; } = true;

        [System.Web.Script.Serialization.ScriptIgnore]
        public string EffectiveClipsFolder => string.IsNullOrWhiteSpace(ClipsFolder) ? Paths.DefaultClips : ClipsFolder;

        public static Settings Load()
        {
            try
            {
                if (File.Exists(Paths.SettingsFile))
                {
                    var s = Json.Read<Settings>(File.ReadAllText(Paths.SettingsFile));
                    if (s != null) return s;
                }
            }
            catch (Exception ex) { Log.Error("settings load failed", ex); }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Paths.Ensure(Paths.Local);
                string tmp = Paths.SettingsFile + ".tmp";
                File.WriteAllText(tmp, Json.Write(this));
                if (File.Exists(Paths.SettingsFile)) File.Replace(tmp, Paths.SettingsFile, null);
                else File.Move(tmp, Paths.SettingsFile);
            }
            catch (Exception ex) { Log.Error("settings save failed", ex); }
        }

        public Settings Clone() { return Json.Read<Settings>(Json.Write(this)); }
    }
}
