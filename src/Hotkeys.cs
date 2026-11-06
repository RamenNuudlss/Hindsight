using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace Hindsight
{
    /// <summary>System-wide hotkeys via RegisterHotKey, so they work while a game has focus.</summary>
    internal sealed class Hotkeys : NativeWindow, IDisposable
    {
        readonly Dictionary<int, Action> actions = new Dictionary<int, Action>();
        int nextId = 1;

        public Hotkeys() { CreateHandle(new CreateParams()); }

        /// <summary>Registers a combo like "Alt+F10". Returns false if the text is invalid or another app owns it.</summary>
        public bool Register(string combo, Action action)
        {
            uint mods, vk;
            if (!Parse(combo, out mods, out vk)) return false;
            int id = nextId++;
            if (!Native.RegisterHotKey(Handle, id, mods | Native.MOD_NOREPEAT, vk))
            {
                Log.Warn("hotkey taken: " + combo);
                return false;
            }
            actions[id] = action;
            return true;
        }

        public void Clear()
        {
            foreach (var id in actions.Keys) Native.UnregisterHotKey(Handle, id);
            actions.Clear();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                Action a;
                if (actions.TryGetValue(m.WParam.ToInt32(), out a))
                {
                    try { a(); } catch (Exception ex) { Log.Error("hotkey action", ex); }
                }
                return;
            }
            base.WndProc(ref m);
        }

        public static bool Parse(string combo, out uint mods, out uint vk)
        {
            mods = 0; vk = 0;
            if (string.IsNullOrWhiteSpace(combo)) return false;
            foreach (var raw in combo.Split('+'))
            {
                string p = raw.Trim();
                switch (p.ToLowerInvariant())
                {
                    case "ctrl": case "control": mods |= Native.MOD_CONTROL; continue;
                    case "alt": mods |= Native.MOD_ALT; continue;
                    case "shift": mods |= Native.MOD_SHIFT; continue;
                    case "win": mods |= Native.MOD_WIN; continue;
                }
                Keys k;
                if (p.Length == 1 && char.IsDigit(p[0])) k = (Keys)Enum.Parse(typeof(Keys), "D" + p);
                else if (!Enum.TryParse(p, true, out k)) return false;
                vk = (uint)k;
            }
            return vk != 0;
        }

        public void Dispose() { Clear(); DestroyHandle(); }
    }

    /// <summary>Remembers the last app window that had focus that isn't ours — that's the game we name clips after.</summary>
    internal sealed class ForegroundTracker : IDisposable
    {
        readonly Native.WinEventDelegate callback;
        IntPtr hook;
        readonly uint ownPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

        public IntPtr LastWindow { get; private set; }

        // The last fullscreen app that had focus, and when it last did. Used to name a clip after the game
        // even when you alt-tabbed out (to Discord, TeamSpeak, a browser) before pressing save.
        IntPtr lastFullscreen;
        DateTime lastFullscreenSeen = DateTime.MinValue;
        readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer { Interval = 2000 };

        public ForegroundTracker()
        {
            callback = OnEvent;
            hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, callback, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
            Remember(Native.GetForegroundWindow());
            poll.Tick += (s, e) => Remember(Native.GetForegroundWindow()); // a game can go fullscreen after it gets focus
            poll.Start();
        }

        /// <summary>
        /// The window to name a clip after: the app in focus if it's fullscreen; otherwise a fullscreen app
        /// that had focus within the clip's time span; otherwise whatever is in focus.
        /// </summary>
        public IntPtr ForClip(int clipSeconds)
        {
            IntPtr cur = Current;
            if (Native.IsFullscreen(cur)) return cur;
            if (lastFullscreen != Native.GetDesktopShell() && Native.IsWindowVisible(lastFullscreen) &&
                (DateTime.UtcNow - lastFullscreenSeen).TotalSeconds <= clipSeconds + 15)
                return lastFullscreen;
            return cur;
        }

        void OnEvent(IntPtr h, uint e, IntPtr hwnd, int obj, int child, uint thread, uint time) { Remember(hwnd); }

        void Remember(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            if (pid == ownPid || pid == 0) return;
            LastWindow = hwnd;
            if (Native.IsFullscreen(hwnd)) { lastFullscreen = hwnd; lastFullscreenSeen = DateTime.UtcNow; }
        }

        public IntPtr Current
        {
            get
            {
                var fg = Native.GetForegroundWindow();
                uint pid;
                Native.GetWindowThreadProcessId(fg, out pid);
                return pid == ownPid || fg == IntPtr.Zero ? LastWindow : fg;
            }
        }

        public void Dispose() { poll.Dispose(); if (hook != IntPtr.Zero) Native.UnhookWinEvent(hook); hook = IntPtr.Zero; }
    }
}
