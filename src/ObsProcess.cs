using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace Hindsight
{
    /// <summary>Starts and stops the background OBS instance.</summary>
    internal static class ObsProcess
    {
        public static Process Running()
        {
            try { return Process.GetProcessesByName("obs64").FirstOrDefault(); }
            catch { return null; }
        }

        public static Process Launch(string obsExe)
        {
            ObsFiles.SaveUserPointers();
            string args = "--profile \"" + Paths.ObsProfile + "\" --collection \"" + Paths.ObsCollection + "\"" +
                          " --minimize-to-tray --disable-shutdown-check --disable-updater --disable-missing-files-check";
            var psi = new ProcessStartInfo(obsExe, args)
            {
                WorkingDirectory = Path.GetDirectoryName(obsExe),
                UseShellExecute = false,
            };
            Log.Info("launching OBS: " + obsExe + " " + args);
            return Process.Start(psi);
        }

        /// <summary>Starts the person's own OBS, exactly as they'd open it themselves (their last profile and scenes).</summary>
        public static Process LaunchPlain(string obsExe)
        {
            var psi = new ProcessStartInfo(obsExe, "--disable-shutdown-check")
            {
                WorkingDirectory = Path.GetDirectoryName(obsExe),
                UseShellExecute = false,
            };
            Log.Info("launching the user's OBS: " + obsExe);
            return Process.Start(psi);
        }

        public static bool Alive(int pid)
        {
            try { return Process.GetProcesses().Any(p => p.Id == pid); }
            catch { return false; }
        }

        /// <summary>Asks OBS to quit like clicking its close button, then forces it after a timeout.</summary>
        public static bool Close(int timeoutMs = 15000)
        {
            var p = Running();
            if (p == null) return true;
            try
            {
                foreach (var hwnd in MainWindows((uint)p.Id))
                    Native.PostMessage(hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                if (p.WaitForExit(timeoutMs)) return true;
                Log.Warn("OBS didn't close in time, ending it");
                p.Kill();
                return p.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                Log.Warn("closing OBS failed: " + ex.Message);
                return false;
            }
        }

        static List<IntPtr> MainWindows(uint pid)
        {
            var found = new List<IntPtr>();
            Native.EnumWindows((h, l) =>
            {
                uint wpid;
                Native.GetWindowThreadProcessId(h, out wpid);
                if (wpid == pid)
                {
                    string title = Native.WindowTitle(h);
                    string cls = Native.WindowClass(h);
                    if (cls.StartsWith("Qt", StringComparison.Ordinal) && title.StartsWith("OBS", StringComparison.Ordinal)) found.Add(h);
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>Brings the OBS window up so the person can poke at it directly.</summary>
        public static void ShowWindow()
        {
            var p = Running();
            if (p == null) return;
            foreach (var h in MainWindows((uint)p.Id))
            {
                Native.ShowWindow(h, Native.SW_RESTORE);
                Native.SetForegroundWindow(h);
            }
        }
    }
}
