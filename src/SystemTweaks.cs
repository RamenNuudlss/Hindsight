using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace Hindsight
{
    /// <summary>Windows-side changes: Game Bar capture, start with Windows, admin task, Apps &amp; features entry.</summary>
    internal static class SystemTweaks
    {
        const string TaskName = "Hindsight";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Hindsight";

        public static bool IsElevated
        {
            get
            {
                try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
                catch { return false; }
            }
        }

        // ---------- Game Bar ----------

        static readonly string[][] GameBarValues =
        {
            new[] { @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled" },
            new[] { @"System\GameConfigStore", "GameDVR_Enabled" },
        };

        public static void DisableGameBarCapture()
        {
            foreach (var v in GameBarValues)
            {
                try
                {
                    using (var k = Registry.CurrentUser.CreateSubKey(v[0]))
                    {
                        object old = k.GetValue(v[1]);
                        ObsFiles.Remember("reg|" + v[0] + "|" + v[1], old == null ? null : Convert.ToString(old));
                        k.SetValue(v[1], 0, RegistryValueKind.DWord);
                    }
                }
                catch (Exception ex) { Log.Warn("game bar: " + ex.Message); }
            }
        }

        public static void RestoreGameBarCapture()
        {
            foreach (var v in GameBarValues)
            {
                string old;
                if (!ObsFiles.Recall("reg|" + v[0] + "|" + v[1], out old)) continue;
                try
                {
                    using (var k = Registry.CurrentUser.CreateSubKey(v[0]))
                    {
                        if (old == null) k.DeleteValue(v[1], false);
                        else k.SetValue(v[1], int.Parse(old), RegistryValueKind.DWord);
                    }
                }
                catch (Exception ex) { Log.Warn("game bar restore: " + ex.Message); }
            }
        }

        // ---------- install location ----------

        /// <summary>Copies the running exe to %LOCALAPPDATA%\Programs\Hindsight so startup doesn't depend on Downloads.</summary>
        public static string InstallSelf()
        {
            string target = Paths.InstalledExe;
            string current = Paths.CurrentExe;
            if (string.Equals(Path.GetFullPath(current), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return target;
            Paths.Ensure(Paths.InstallDir);
            try { File.Copy(current, target, true); }
            catch (Exception ex)
            {
                // A copy may already be running (e.g. an older version); keep using the current one.
                Log.Warn("install copy failed: " + ex.Message);
                if (!File.Exists(target)) return current;
            }
            return target;
        }

        public static void RegisterApp(string exe)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
                {
                    k.SetValue("DisplayName", "Hindsight");
                    k.SetValue("DisplayIcon", exe + ",0");
                    k.SetValue("DisplayVersion", Program.Version);
                    k.SetValue("Publisher", "RamenNuudls");
                    k.SetValue("InstallLocation", Path.GetDirectoryName(exe));
                    k.SetValue("UninstallString", "\"" + exe + "\" --uninstall");
                    k.SetValue("URLInfoAbout", Paths.RepoUrl);
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    k.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
                }
            }
            catch (Exception ex) { Log.Warn("apps entry: " + ex.Message); }
            CreateStartMenuShortcut(exe);
        }

        public static void UnregisterApp()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            try { File.Delete(ShortcutPath); } catch { }
        }

        static string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Hindsight.lnk");

        static void CreateStartMenuShortcut(string exe)
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(shellType);
                object lnk = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { ShortcutPath });
                var t = lnk.GetType();
                t.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { exe });
                t.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(exe) });
                t.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { "Save the last few minutes of gameplay" });
                t.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { exe + ",0" });
                t.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, lnk, null);
            }
            catch (Exception ex) { Log.Warn("shortcut: " + ex.Message); }
        }

        // ---------- startup ----------

        public static void SetRunKey(string exe, bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue("Hindsight", "\"" + exe + "\" --startup");
                    else k.DeleteValue("Hindsight", false);
                }
            }
            catch (Exception ex) { Log.Warn("run key: " + ex.Message); }
        }

        public static bool TaskExists()
        {
            return RunHidden("schtasks.exe", "/Query /TN \"" + TaskName + "\"") == 0;
        }

        /// <summary>Starts the elevated copy through the scheduled task (no UAC prompt once the task exists).</summary>
        public static bool RunTask()
        {
            return RunHidden("schtasks.exe", "/Run /TN \"" + TaskName + "\"") == 0;
        }

        /// <summary>Creates or removes the admin task. Needs elevation, so it relaunches itself with a UAC prompt when necessary.</summary>
        public static bool ConfigureTask(string exe, bool create, bool atLogon)
        {
            string args = "--task " + (create ? "create" : "delete") + " " + (atLogon ? "logon" : "manual") + " \"" + exe + "\"";
            if (IsElevated) return Elevated.RunTask(Elevated.SplitArgs(args)) == 0;
            try
            {
                var psi = new ProcessStartInfo(Paths.CurrentExe, args) { UseShellExecute = true, Verb = "runas" };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit(60000);
                    return p.ExitCode == 0;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Log.Info("admin prompt declined");
                return false;
            }
        }

        public static string TaskXml(string exe, bool atLogon)
        {
            string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
            string trigger = atLogon
                ? "<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId><Delay>PT5S</Delay></LogonTrigger></Triggers>"
                : "<Triggers />";
            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                   "<Task version=\"1.3\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
                   "<RegistrationInfo><Description>Starts Hindsight and its background OBS with administrator rights.</Description></RegistrationInfo>" +
                   trigger +
                   "<Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
                   "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                   "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>true</AllowHardTerminate><StartWhenAvailable>false</StartWhenAvailable>" +
                   "<RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable><IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>" +
                   "<AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>false</Hidden><RunOnlyIfIdle>false</RunOnlyIfIdle>" +
                   "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>5</Priority></Settings>" +
                   "<Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(exe) + "</Command><Arguments>--startup</Arguments>" +
                   "<WorkingDirectory>" + SecurityElement.Escape(Path.GetDirectoryName(exe)) + "</WorkingDirectory></Exec></Actions></Task>";
        }

        public static int RunHidden(string file, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.WaitForExit(30000);
                    return p.ExitCode;
                }
            }
            catch (Exception ex) { Log.Warn(file + " failed: " + ex.Message); return -1; }
        }

        /// <summary>Deletes the installed exe after this process exits (it can't delete itself while running).</summary>
        public static void ScheduleSelfDelete()
        {
            try
            {
                string dir = Paths.InstallDir;
                if (!Directory.Exists(dir)) return;
                string cmd = "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + dir + "\"";
                Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch { }
        }
    }

    /// <summary>The small piece that runs with a UAC prompt: creating or deleting the admin task.</summary>
    internal static class Elevated
    {
        public static string[] SplitArgs(string s)
        {
            var list = new System.Collections.Generic.List<string>();
            var cur = new StringBuilder();
            bool q = false;
            foreach (char c in s)
            {
                if (c == '"') { q = !q; continue; }
                if (c == ' ' && !q) { if (cur.Length > 0) { list.Add(cur.ToString()); cur.Clear(); } continue; }
                cur.Append(c);
            }
            if (cur.Length > 0) list.Add(cur.ToString());
            return list.ToArray();
        }

        /// <summary>args: --task create|delete logon|manual "exe path"</summary>
        public static int RunTask(string[] args)
        {
            try
            {
                if (args.Length < 4) return 2;
                bool create = args[1] == "create";
                bool logon = args[2] == "logon";
                string exe = args[3];
                if (!create)
                {
                    int r = SystemTweaks.RunHidden("schtasks.exe", "/Delete /TN \"Hindsight\" /F");
                    return r == 0 || !SystemTweaks.TaskExists() ? 0 : r;
                }
                string xml = Path.Combine(Path.GetTempPath(), "hindsight-task.xml");
                File.WriteAllText(xml, SystemTweaks.TaskXml(exe, logon), Encoding.Unicode);
                int rc = SystemTweaks.RunHidden("schtasks.exe", "/Create /TN \"Hindsight\" /XML \"" + xml + "\" /F");
                try { File.Delete(xml); } catch { }
                return rc;
            }
            catch (Exception ex)
            {
                Log.Error("elevated task", ex);
                return 1;
            }
        }
    }
}
