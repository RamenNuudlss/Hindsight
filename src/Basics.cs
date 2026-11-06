using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace Hindsight
{
    internal static class Paths
    {
        public const string AppName = "Hindsight";
        public const string ObsProfile = "Hindsight";
        public const string ObsCollection = "Hindsight";
        public const string RepoUrl = "https://github.com/RamenNuudlss/Hindsight";

        public static string Local => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
        public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
        public static string InstalledExe => Path.Combine(InstallDir, AppName + ".exe");
        public static string SettingsFile => Path.Combine(Local, "settings.json");
        public static string BackupFile => Path.Combine(Local, "restore.json");
        public static string OpenFlag => Path.Combine(Local, "open-window.flag");
        public static string LogFile => Path.Combine(Local, "hindsight.log");
        // Separate folders: an admin and a normal WebView2 can't share one while the other is shutting down.
        public static string WebViewData => Path.Combine(Local, SystemTweaks.IsElevated ? "webview-admin" : "webview");
        public static string UiDir => Path.Combine(Local, "ui");
        public static string ThumbDir => Path.Combine(Local, "thumbs");
        public static string RuntimeDir => Path.Combine(Local, "runtime");

        public static string ObsConfig => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio");
        public static string ObsProfileDir => Path.Combine(ObsConfig, "basic", "profiles", ObsProfile);
        public static string ObsCollectionFile => Path.Combine(ObsConfig, "basic", "scenes", ObsCollection + ".json");
        public static string ObsWebsocketConfig => Path.Combine(ObsConfig, "plugin_config", "obs-websocket", "config.json");

        public static string DefaultClips => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Clips");

        public static string CurrentExe => System.Windows.Forms.Application.ExecutablePath;

        public static void Ensure(string dir)
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }
    }

    internal static class Log
    {
        static readonly object Gate = new object();

        public static void Init()
        {
            try
            {
                Paths.Ensure(Paths.Local);
                var fi = new FileInfo(Paths.LogFile);
                if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                {
                    string old = Paths.LogFile + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(Paths.LogFile, old);
                }
            }
            catch { }
        }

        public static void Info(string msg) { Write("info", msg); }
        public static void Warn(string msg) { Write("warn", msg); }
        public static void Error(string msg, Exception ex = null) { Write("error", ex == null ? msg : msg + " :: " + ex); }

        static void Write(string level, string msg)
        {
            try
            {
                lock (Gate)
                    File.AppendAllText(Paths.LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " + msg + Environment.NewLine);
            }
            catch { }
        }
    }

    /// <summary>Small JSON helper over the framework's built-in serializer.</summary>
    internal static class Json
    {
        static JavaScriptSerializer Make()
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };
        }

        public static string Write(object o) { return Make().Serialize(o); }

        public static Dictionary<string, object> Parse(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return new Dictionary<string, object>();
            var o = Make().DeserializeObject(s) as Dictionary<string, object>;
            return o ?? new Dictionary<string, object>();
        }

        public static T Read<T>(string s) { return Make().Deserialize<T>(s); }

        public static object Get(this Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }

        public static string Str(this Dictionary<string, object> d, string key, string def = null)
        {
            var v = d.Get(key);
            return v == null ? def : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static bool Bool(this Dictionary<string, object> d, string key, bool def = false)
        {
            var v = d.Get(key);
            if (v is bool) return (bool)v;
            if (v is string) { bool b; if (bool.TryParse((string)v, out b)) return b; }
            return def;
        }

        public static int Int(this Dictionary<string, object> d, string key, int def = 0)
        {
            var v = d.Get(key);
            if (v == null) return def;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch { return def; }
        }

        public static double Num(this Dictionary<string, object> d, string key, double def = 0)
        {
            var v = d.Get(key);
            if (v == null) return def;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return def; }
        }

        public static Dictionary<string, object> Obj(this Dictionary<string, object> d, string key)
        {
            return d.Get(key) as Dictionary<string, object>;
        }

        public static List<object> Arr(this Dictionary<string, object> d, string key)
        {
            var v = d.Get(key);
            if (v is ArrayList) return ((ArrayList)v).Cast<object>().ToList();
            if (v is object[]) return ((object[])v).ToList();
            return new List<object>();
        }
    }

    /// <summary>
    /// Order-preserving INI editor. OBS's config files are plain INI written as UTF-8,
    /// so we only touch the keys we need and leave everything else exactly as it was.
    /// </summary>
    internal sealed class IniFile
    {
        readonly List<string> lines = new List<string>();
        public string Path { get; private set; }

        public IniFile(string path)
        {
            Path = path;
            if (File.Exists(path))
                lines.AddRange(File.ReadAllLines(path, Encoding.UTF8));
        }

        public static IniFile Empty(string path)
        {
            var f = new IniFile(null);
            f.Path = path;
            return f;
        }

        int SectionStart(string section)
        {
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].Trim().Equals("[" + section + "]", StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        int SectionEnd(int start)
        {
            int i = start + 1;
            while (i < lines.Count && !lines[i].TrimStart().StartsWith("[")) i++;
            return i;
        }

        int KeyLine(string section, string key)
        {
            int s = SectionStart(section);
            if (s < 0) return -1;
            int e = SectionEnd(s);
            for (int i = s + 1; i < e; i++)
            {
                int eq = lines[i].IndexOf('=');
                if (eq > 0 && lines[i].Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        public string Get(string section, string key)
        {
            int i = KeyLine(section, key);
            if (i < 0) return null;
            return lines[i].Substring(lines[i].IndexOf('=') + 1);
        }

        public void Set(string section, string key, string value)
        {
            int i = KeyLine(section, key);
            if (i >= 0) { lines[i] = key + "=" + value; return; }
            int s = SectionStart(section);
            if (s < 0)
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) lines.Add("");
                lines.Add("[" + section + "]");
                lines.Add(key + "=" + value);
                return;
            }
            int e = SectionEnd(s);
            // insert before trailing blank lines of the section
            int at = e;
            while (at - 1 > s && lines[at - 1].Trim().Length == 0) at--;
            lines.Insert(at, key + "=" + value);
        }

        public void Remove(string section, string key)
        {
            int i = KeyLine(section, key);
            if (i >= 0) lines.RemoveAt(i);
        }

        /// <summary>Set the value, or remove the key when value is null (used when restoring).</summary>
        public void SetOrRemove(string section, string key, string value)
        {
            if (value == null) Remove(section, key); else Set(section, key, value);
        }

        public void Save()
        {
            Paths.Ensure(System.IO.Path.GetDirectoryName(Path));
            File.WriteAllLines(Path, lines, new UTF8Encoding(false));
        }
    }

    internal static class Fmt
    {
        public static string Duration(int seconds)
        {
            if (seconds < 60) return seconds + " sec";
            if (seconds % 60 == 0) return (seconds / 60) + " min";
            return (seconds / 60) + " min " + (seconds % 60) + " sec";
        }

        public static string Bytes(long b)
        {
            if (b >= 1L << 30) return (b / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
            if (b >= 1L << 20) return (b / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture) + " MB";
            return Math.Max(1, b / 1024) + " KB";
        }
    }
}
