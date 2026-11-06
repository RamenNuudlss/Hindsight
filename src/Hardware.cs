using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Windows.Forms;

namespace Hindsight
{
    public sealed class EncoderOption
    {
        public string Key;        // stable key stored in settings, e.g. "nvenc_h264"
        public string Label;      // "NVIDIA NVENC"
        public string Codec;      // H.264 | HEVC | AV1
        public string Vendor;     // nvidia | amd | intel | cpu
        public string Note;       // shown under the option
        public bool Hardware => Vendor != "cpu";

        public string ObsId(Version obs)
        {
            bool newNvenc = obs != null && (obs.Major > 30 || (obs.Major == 30 && obs.Minor >= 2));
            switch (Key)
            {
                case "nvenc_h264": return newNvenc ? "obs_nvenc_h264_tex" : "jim_nvenc";
                case "nvenc_hevc": return newNvenc ? "obs_nvenc_hevc_tex" : "jim_hevc_nvenc";
                case "nvenc_av1": return newNvenc ? "obs_nvenc_av1_tex" : "jim_av1_nvenc";
                case "amf_h264": return "h264_texture_amf";
                case "amf_hevc": return "h265_texture_amf";
                case "amf_av1": return "av1_texture_amf";
                case "qsv_h264": return "obs_qsv11_v2";
                case "qsv_hevc": return "obs_qsv11_hevc";
                case "qsv_av1": return "obs_qsv11_av1";
                default: return "obs_x264";
            }
        }
    }

    public sealed class MonitorInfo
    {
        public string Name;
        public int Index;
        public int Width, Height, Hz;
        public bool Primary;
    }

    /// <summary>What the PC can do: GPUs, screens, memory, and which OBS encoders make sense.</summary>
    public static class Hardware
    {
        static List<string> gpus;

        public static List<string> Gpus
        {
            get
            {
                if (gpus != null) return gpus;
                gpus = new List<string>();
                try
                {
                    using (var q = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
                        foreach (ManagementObject o in q.Get())
                        {
                            string n = Convert.ToString(o["Name"]);
                            if (!string.IsNullOrWhiteSpace(n)) gpus.Add(n.Trim());
                        }
                }
                catch (Exception ex) { Log.Warn("gpu query failed: " + ex.Message); }
                Log.Info("GPUs: " + string.Join(", ", gpus));
                return gpus;
            }
        }

        static bool Has(string vendorWord) { return Gpus.Any(g => g.IndexOf(vendorWord, StringComparison.OrdinalIgnoreCase) >= 0); }

        static bool AnyMatch(params string[] words)
        {
            return Gpus.Any(g => words.Any(w => g.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0));
        }

        public static List<EncoderOption> Encoders()
        {
            var list = new List<EncoderOption>();
            bool nvidia = Has("NVIDIA");
            bool amd = Has("AMD") || Has("Radeon");
            bool intel = Has("Intel");

            if (nvidia)
            {
                list.Add(new EncoderOption { Key = "nvenc_h264", Label = "NVIDIA NVENC", Codec = "H.264", Vendor = "nvidia", Note = "Plays everywhere. Best for sharing." });
                if (!AnyMatch("GTX 7", "GTX 6", "GT 7", "GT 6"))
                    list.Add(new EncoderOption { Key = "nvenc_hevc", Label = "NVIDIA NVENC", Codec = "HEVC", Vendor = "nvidia", Note = "Smaller files. Some players need the HEVC extension." });
                if (AnyMatch("RTX 40", "RTX 50", "RTX PRO", "RTX 2000 Ada", "RTX 4000 Ada", "RTX 5000 Ada", "RTX 6000 Ada"))
                    list.Add(new EncoderOption { Key = "nvenc_av1", Label = "NVIDIA NVENC", Codec = "AV1", Vendor = "nvidia", Note = "Smallest files at the same quality." });
            }
            if (amd)
            {
                list.Add(new EncoderOption { Key = "amf_h264", Label = "AMD AMF", Codec = "H.264", Vendor = "amd", Note = "Plays everywhere. Best for sharing." });
                list.Add(new EncoderOption { Key = "amf_hevc", Label = "AMD AMF", Codec = "HEVC", Vendor = "amd", Note = "Smaller files. Some players need the HEVC extension." });
                if (AnyMatch("RX 7", "RX 9", "780M", "880M", "890M", "8060S"))
                    list.Add(new EncoderOption { Key = "amf_av1", Label = "AMD AMF", Codec = "AV1", Vendor = "amd", Note = "Smallest files at the same quality." });
            }
            if (intel)
            {
                list.Add(new EncoderOption { Key = "qsv_h264", Label = "Intel Quick Sync", Codec = "H.264", Vendor = "intel", Note = "Plays everywhere. Best for sharing." });
                list.Add(new EncoderOption { Key = "qsv_hevc", Label = "Intel Quick Sync", Codec = "HEVC", Vendor = "intel", Note = "Smaller files. Some players need the HEVC extension." });
                if (AnyMatch("Arc", "Core Ultra"))
                    list.Add(new EncoderOption { Key = "qsv_av1", Label = "Intel Quick Sync", Codec = "AV1", Vendor = "intel", Note = "Smallest files at the same quality." });
            }
            list.Add(new EncoderOption { Key = "x264", Label = "CPU (x264)", Codec = "H.264", Vendor = "cpu", Note = "Uses your processor. Can cost FPS in games." });
            return list;
        }

        public static EncoderOption PickEncoder(string key)
        {
            var all = Encoders();
            var hit = all.FirstOrDefault(e => e.Key == key);
            return hit ?? all.First(); // first entry is the best hardware H.264 encoder
        }

        public static List<MonitorInfo> Monitors()
        {
            var list = new List<MonitorInfo>();
            var screens = Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                var s = screens[i];
                var dm = new Native.DEVMODE();
                dm.dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.DEVMODE));
                int w = s.Bounds.Width, h = s.Bounds.Height, hz = 60;
                try
                {
                    if (Native.EnumDisplaySettings(s.DeviceName, Native.ENUM_CURRENT_SETTINGS, ref dm))
                    {
                        if (dm.dmPelsWidth > 0) { w = dm.dmPelsWidth; h = dm.dmPelsHeight; }
                        if (dm.dmDisplayFrequency > 1) hz = dm.dmDisplayFrequency;
                    }
                }
                catch (Exception ex) { Log.Warn("display mode: " + ex.Message); }
                list.Add(new MonitorInfo { Name = "Display " + (i + 1), Index = i, Width = w, Height = h, Hz = hz, Primary = s.Primary });
            }
            return list;
        }

        public static MonitorInfo PrimaryMonitor()
        {
            var m = Monitors();
            return m.FirstOrDefault(x => x.Primary) ?? m.FirstOrDefault() ?? new MonitorInfo { Width = 1920, Height = 1080, Hz = 60, Primary = true };
        }

        public static long TotalRamBytes()
        {
            try
            {
                var m = new Native.MEMORYSTATUSEX();
                if (Native.GlobalMemoryStatusEx(m)) return (long)m.ullTotalPhys;
            }
            catch { }
            return 16L << 30;
        }

        /// <summary>Video bitrate in kbps for a quality preset, scaled to resolution, frame rate and codec.</summary>
        public static int Bitrate(string quality, int width, int height, int fps, string codec)
        {
            double basis; // kbps for 1080p60 H.264
            switch (quality)
            {
                case "low": basis = 10000; break;
                case "medium": basis = 18000; break;
                case "max": basis = 50000; break;
                default: basis = 30000; break;
            }
            double pixelRate = (double)width * height * Math.Max(24, fps);
            double ref1080 = 1920.0 * 1080 * 60;
            double scaled = basis * Math.Pow(pixelRate / ref1080, 0.75);
            if (codec == "HEVC") scaled *= 0.72;
            else if (codec == "AV1") scaled *= 0.65;
            return (int)Math.Max(2500, Math.Min(150000, Math.Round(scaled / 500.0) * 500));
        }

        /// <summary>Memory OBS needs to hold the buffer, in MB, with headroom for audio and keyframes.</summary>
        public static int BufferMegabytes(int kbps, int seconds)
        {
            double mb = (kbps + 600) * 1000.0 / 8 * seconds / (1024 * 1024);
            return (int)Math.Ceiling(mb * 1.3) + 64;
        }

        public static Version ObsVersion(string obsExe)
        {
            try
            {
                var fv = FileVersionInfo.GetVersionInfo(obsExe);
                return new Version(fv.FileMajorPart, fv.FileMinorPart, Math.Max(0, fv.FileBuildPart));
            }
            catch { return null; }
        }

        public static void Size(int height, MonitorInfo m, out int w, out int h)
        {
            if (height <= 0 || height >= m.Height) { w = m.Width; h = m.Height; }
            else
            {
                h = height;
                w = (int)Math.Round(m.Width * (height / (double)m.Height));
            }
            w -= w % 2; h -= h % 2;
        }
    }
}
