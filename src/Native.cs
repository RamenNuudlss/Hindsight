using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Hindsight
{
    internal static class Native
    {
        public const int WM_HOTKEY = 0x0312;
        public const int WM_CLOSE = 0x0010;
        public const int WM_NCLBUTTONDOWN = 0x00A1;
        public const int HTCAPTION = 2;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int CS_DROPSHADOW = 0x00020000;
        public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        public const uint WINEVENT_OUTOFCONTEXT = 0;
        public const int SW_SHOWNOACTIVATE = 4;
        public const int SW_RESTORE = 9;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int max);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int cmd);

        [DllImport("user32.dll")]
        public static extern bool AllowSetForegroundWindow(int pid);

        public delegate void WinEventDelegate(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll")]
        public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventDelegate cb, uint pid, uint tid, uint flags);

        [DllImport("user32.dll")]
        public static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern bool GetMonitorInfo(IntPtr hmon, ref MONITORINFO info);

        /// <summary>True when a window covers its whole monitor: exclusive or borderless fullscreen, i.e. almost always a game or video.</summary>
        public static bool IsFullscreen(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd)) return false;
                string cls = WindowClass(hwnd);
                if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd") return false; // the desktop itself
                RECT r;
                if (!GetWindowRect(hwnd, out r)) return false;
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref mi)) return false;
                return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
            }
            catch { return false; }
        }

        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr hmon, int type, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr h);

        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX buffer);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields;
            public int dmPositionX, dmPositionY;
            public int dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        public const int ENUM_CURRENT_SETTINGS = -1;

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [StructLayout(LayoutKind.Sequential)]
        public struct MARGINS { public int Left, Right, Top, Bottom; }

        [DllImport("dwmapi.dll")]
        public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);

        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWA_BORDER_COLOR = 34;
        public const int DWMWCP_ROUND = 2;
        public const int DWMWCP_ROUNDSMALL = 3;

        public static void RoundCorners(IntPtr hwnd, bool small = false)
        {
            try
            {
                int pref = small ? DWMWCP_ROUNDSMALL : DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4);
                int dark = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
                int border = 0x003B2C2A; // COLORREF (BGR) for #2a2c3b-ish, subtle edge on Win11
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, 4);
            }
            catch { }
        }

        public static string ProcessPath(uint pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        [DllImport("user32.dll")]
        static extern IntPtr GetShellWindow();

        public static IntPtr GetDesktopShell() { try { return GetShellWindow(); } catch { return IntPtr.Zero; } }

        public static string WindowTitle(IntPtr hwnd)
        {
            var sb = new StringBuilder(512);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string WindowClass(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static float DpiScaleFor(IntPtr hwnd)
        {
            try
            {
                IntPtr mon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
                uint x, y;
                if (GetDpiForMonitor(mon, 0, out x, out y) == 0 && x > 0) return x / 96f;
            }
            catch { }
            return 1f;
        }
    }
}
