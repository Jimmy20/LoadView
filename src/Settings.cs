using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;

namespace LoadView
{
    // All persisted configuration, stored as a tiny INI in %APPDATA%\LoadView\settings.ini.
    // Missing/corrupt values fall back to defaults. Sizes are base pixels at 96 dpi (scaled
    // at layout); font sizes are in points; colors are hex RRGGBB.
    internal sealed class Settings
    {
        // ---- section keys (also the canonical/default order) ----
        public const string SecClock = "clock";
        public const string SecCpu = "cpu";
        public const string SecGpu = "gpu";
        public const string SecMem = "mem";
        public const string SecDisk = "disk";
        public const string SecNet = "net";
        public const string SecNetTotals = "nettotals";
        public const string SecTopCpu = "topcpu";
        public const string SecTopRam = "topram";
        public const string SecDrives = "drives";
        public const string SecIp = "ip";
        public const string SecFooter = "footer";

        public static readonly string[] AllSections = new string[]
        {
            SecClock, SecCpu, SecGpu, SecMem, SecDisk, SecNet,
            SecNetTotals, SecTopCpu, SecTopRam, SecDrives, SecIp, SecFooter
        };

        // window / position
        public bool HasPosition;
        public int X;
        public int Y;
        public double Opacity = 0.9;

        // layout
        public int Width = 250;
        public int GraphHeight = 120;
        public int DriveRowHeight = 50;
        public List<string> Order = new List<string>(AllSections);

        // Remembered window position per display signature (resolution/layout) -> {x,y}.
        public Dictionary<string, int[]> Positions = new Dictionary<string, int[]>();

        // clock / date / weekday
        public bool ShowSeconds = false;
        public float ClockSize = 50f;
        public float DateSize = 20f;
        public float DaySize = 25f;
        public bool DateBold = true;
        public bool DayBold = true;
        public Color ClockColor = Color.FromArgb(255, 255, 255);
        public Color DateColor = Color.FromArgb(232, 232, 237);
        public Color DayColor = Color.FromArgb(255, 255, 128);

        // drives
        public float DriveLabelSize = 9f;
        public bool DriveLabelBold = false;

        // process lists / IP / net-totals text size
        public float ListSize = 11f;
        public float IpSize = 11f;
        public float NetTotalsSize = 12f;

        // network
        public bool NetUnitBytes = true;       // true: MB/s & kB/s ; false: Mbps & Kbps
        public bool ExternalIpEnabled = true;
        public int IpLanRefreshSec = 10;
        public int IpWanRefreshSec = 600;

        // temperatures
        public bool TempFahrenheit = false;        // false: show °C, true: show °F
        public bool ShowCpuTemp = true;            // append CPU temp to the CPU graph readout
        public bool ShowGpuTemp = true;            // append GPU temp to the GPU graph readout
        public double TempHotC = 0;                // temp >= this many °C shows in red; 0 = off
        public bool AccurateCpuTempDriver = false; // opt-in: kernel-driver CPU temp (downloaded on enable)

        // per-graph accent colors
        public Color CpuColor = Color.FromArgb(0x4F, 0x8C, 0xFF);
        public Color GpuColor = Color.FromArgb(0x36, 0xC7, 0x9B);
        public Color MemColor = Color.FromArgb(0xB0, 0x7C, 0xFF);
        public Color DiskColor = Color.FromArgb(0x6F, 0xD0, 0x57);
        public Color NetDownColor = Color.FromArgb(0x57, 0xD0, 0x6F); // download = green
        public Color NetUpColor = Color.FromArgb(0xE0, 0x5A, 0x5A);   // upload = red

        // per-graph ceiling (0 = auto / 100 for % graphs) and red-alert threshold (0 = off)
        public double CpuMax = 100, GpuMax = 100, MemMax = 100, DiskMax = 100, NetMax = 0;
        public double CpuAlert = 90, GpuAlert = 90, MemAlert = 90, DiskAlert = 90, NetAlert = 0;

        // behavior
        public bool AlwaysOnTop = true;
        public bool Locked;
        public int RefreshMs = 1000;
        public bool DebugLog = false;

        // section visibility
        public bool ShowClock = true;
        public bool ShowCpu = true;
        public bool ShowGpu = true;
        public bool ShowMem = true;
        public bool ShowDisk = true;
        public bool ShowNet = true;
        public bool ShowNetTotals = true;
        public bool ShowTopCpu = true;
        public bool ShowTopRam = true;
        public bool ShowDrives = true;
        public bool ShowIp = true;
        public bool ShowFooter = true;

        public Settings Clone()
        {
            Settings s = (Settings)MemberwiseClone();
            s.Order = new List<string>(Order); // deep-copy the mutable list
            s.Positions = new Dictionary<string, int[]>();
            foreach (KeyValuePair<string, int[]> e in Positions)
                s.Positions[e.Key] = new int[] { e.Value[0], e.Value[1] };
            return s;
        }

        public bool TryGetPos(string sig, out int x, out int y)
        {
            int[] v;
            if (sig != null && Positions.TryGetValue(sig, out v) && v != null && v.Length == 2)
            { x = v[0]; y = v[1]; return true; }
            x = 0; y = 0; return false;
        }

        public void SetPos(string sig, int x, int y)
        {
            if (!string.IsNullOrEmpty(sig)) Positions[sig] = new int[] { x, y };
        }

        public bool GetShow(string key)
        {
            switch (key)
            {
                case SecClock: return ShowClock;
                case SecCpu: return ShowCpu;
                case SecGpu: return ShowGpu;
                case SecMem: return ShowMem;
                case SecDisk: return ShowDisk;
                case SecNet: return ShowNet;
                case SecNetTotals: return ShowNetTotals;
                case SecTopCpu: return ShowTopCpu;
                case SecTopRam: return ShowTopRam;
                case SecDrives: return ShowDrives;
                case SecIp: return ShowIp;
                case SecFooter: return ShowFooter;
            }
            return false;
        }

        public void SetShow(string key, bool v)
        {
            switch (key)
            {
                case SecClock: ShowClock = v; break;
                case SecCpu: ShowCpu = v; break;
                case SecGpu: ShowGpu = v; break;
                case SecMem: ShowMem = v; break;
                case SecDisk: ShowDisk = v; break;
                case SecNet: ShowNet = v; break;
                case SecNetTotals: ShowNetTotals = v; break;
                case SecTopCpu: ShowTopCpu = v; break;
                case SecTopRam: ShowTopRam = v; break;
                case SecDrives: ShowDrives = v; break;
                case SecIp: ShowIp = v; break;
                case SecFooter: ShowFooter = v; break;
            }
        }

        public static string DisplayName(string key)
        {
            switch (key)
            {
                case SecClock: return "Clock";
                case SecCpu: return "CPU graph";
                case SecGpu: return "GPU graph";
                case SecMem: return "MEM graph";
                case SecDisk: return "DISK graph";
                case SecNet: return "NET graph";
                case SecNetTotals: return "Net totals";
                case SecTopCpu: return "Top CPU";
                case SecTopRam: return "Top RAM";
                case SecDrives: return "Drives";
                case SecIp: return "IP addresses";
                case SecFooter: return "Date / weekday";
            }
            return key;
        }

        private static string Dir()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LoadView");
        }

        private static string FilePath() { return Path.Combine(Dir(), "settings.ini"); }
        private static string DefaultsPath() { return Path.Combine(Dir(), "defaults.ini"); }

        // Active settings -> defaults.ini fallback (lets you carry a preferred config across
        // machines and "reset to defaults" return to it) -> built-in defaults.
        public static Settings Load()
        {
            string p = FilePath();
            return File.Exists(p) ? LoadFrom(p) : LoadDefaults();
        }

        public static Settings LoadDefaults()
        {
            string p = DefaultsPath();
            return File.Exists(p) ? LoadFrom(p) : new Settings();
        }

        private static Settings LoadFrom(string path)
        {
            Settings s = new Settings();
            Dictionary<string, string> kv = new Dictionary<string, string>();
            try
            {
                foreach (string line in File.ReadAllLines(path))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim();
                    kv[key] = val;
                }
            }
            catch { return s; }

            bool hx = false, hy = false;
            s.X = GetInt(kv, "x", s.X, ref hx);
            s.Y = GetInt(kv, "y", s.Y, ref hy);
            s.HasPosition = hx && hy;
            s.Opacity = GetDouble(kv, "opacity", s.Opacity);

            s.Width = GetInt(kv, "width", s.Width);
            s.GraphHeight = GetInt(kv, "graphheight", s.GraphHeight);
            s.DriveRowHeight = GetInt(kv, "driverowheight", s.DriveRowHeight);
            s.Order = NormalizeOrder(GetString(kv, "order", null));

            s.ShowSeconds = GetBool(kv, "showseconds", s.ShowSeconds);
            s.ClockSize = GetFloat(kv, "clocksize", s.ClockSize);
            s.DateSize = GetFloat(kv, "datesize", s.DateSize);
            s.DaySize = GetFloat(kv, "daysize", s.DaySize);
            s.DateBold = GetBool(kv, "datebold", s.DateBold);
            s.DayBold = GetBool(kv, "daybold", s.DayBold);
            s.ClockColor = GetColor(kv, "clockcolor", s.ClockColor);
            s.DateColor = GetColor(kv, "datecolor", s.DateColor);
            s.DayColor = GetColor(kv, "daycolor", s.DayColor);

            s.DriveLabelSize = GetFloat(kv, "drivelabelsize", s.DriveLabelSize);
            s.DriveLabelBold = GetBool(kv, "drivelabelbold", s.DriveLabelBold);
            s.ListSize = GetFloat(kv, "listsize", s.ListSize);
            s.IpSize = GetFloat(kv, "ipsize", s.IpSize);
            s.NetTotalsSize = GetFloat(kv, "nettotalssize", s.NetTotalsSize);

            s.NetUnitBytes = GetBool(kv, "netunitbytes", s.NetUnitBytes);
            s.ExternalIpEnabled = GetBool(kv, "externalip", s.ExternalIpEnabled);

            s.TempFahrenheit = GetBool(kv, "tempfahrenheit", s.TempFahrenheit);
            s.ShowCpuTemp = GetBool(kv, "showcputemp", s.ShowCpuTemp);
            s.ShowGpuTemp = GetBool(kv, "showgputemp", s.ShowGpuTemp);
            s.TempHotC = GetDouble(kv, "temphotc", s.TempHotC);
            s.AccurateCpuTempDriver = GetBool(kv, "accuratecputempdriver", s.AccurateCpuTempDriver);

            s.CpuColor = GetColor(kv, "cpucolor", s.CpuColor);
            s.GpuColor = GetColor(kv, "gpucolor", s.GpuColor);
            s.MemColor = GetColor(kv, "memcolor", s.MemColor);
            s.DiskColor = GetColor(kv, "diskcolor", s.DiskColor);
            s.NetDownColor = GetColor(kv, "netdowncolor", s.NetDownColor);
            s.NetUpColor = GetColor(kv, "netupcolor", s.NetUpColor);

            s.CpuMax = GetDouble(kv, "cpumax", s.CpuMax);
            s.GpuMax = GetDouble(kv, "gpumax", s.GpuMax);
            s.MemMax = GetDouble(kv, "memmax", s.MemMax);
            s.DiskMax = GetDouble(kv, "diskmax", s.DiskMax);
            s.NetMax = GetDouble(kv, "netmax", s.NetMax);

            s.CpuAlert = GetDouble(kv, "cpualert", s.CpuAlert);
            s.GpuAlert = GetDouble(kv, "gpualert", s.GpuAlert);
            s.MemAlert = GetDouble(kv, "memalert", s.MemAlert);
            s.DiskAlert = GetDouble(kv, "diskalert", s.DiskAlert);
            s.NetAlert = GetDouble(kv, "netalert", s.NetAlert);

            s.AlwaysOnTop = GetBool(kv, "alwaysontop", s.AlwaysOnTop);
            s.Locked = GetBool(kv, "locked", s.Locked);
            s.RefreshMs = GetInt(kv, "refreshms", s.RefreshMs);
            s.DebugLog = GetBool(kv, "debuglog", s.DebugLog);
            s.IpLanRefreshSec = GetInt(kv, "iplanrefreshsec", s.IpLanRefreshSec);
            s.IpWanRefreshSec = GetInt(kv, "ipwanrefreshsec", s.IpWanRefreshSec);

            foreach (string key in AllSections)
                s.SetShow(key, GetBool(kv, "show_" + key, s.GetShow(key)));

            // Remembered positions: keys like "pos.<sig>=x,y"
            foreach (KeyValuePair<string, string> e in kv)
            {
                if (!e.Key.StartsWith("pos.")) continue;
                string sig = e.Key.Substring(4);
                string[] xy = e.Value.Split(',');
                int px, py;
                if (xy.Length == 2 &&
                    int.TryParse(xy[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out px) &&
                    int.TryParse(xy[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out py))
                    s.Positions[sig] = new int[] { px, py };
            }

            return s;
        }

        public void Save() { SaveTo(FilePath(), true); }

        // Persist the current settings as the fallback defaults (position is not saved here,
        // so the default lands top-right on whatever machine loads it).
        public void SaveAsDefaults() { SaveTo(DefaultsPath(), false); }

        private void SaveTo(string path, bool includePosition)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                List<string> l = new List<string>();
                if (includePosition)
                {
                    l.Add("x=" + X.ToString(CultureInfo.InvariantCulture));
                    l.Add("y=" + Y.ToString(CultureInfo.InvariantCulture));
                }
                l.Add("opacity=" + Opacity.ToString("0.00", CultureInfo.InvariantCulture));
                l.Add("width=" + Width.ToString(CultureInfo.InvariantCulture));
                l.Add("graphheight=" + GraphHeight.ToString(CultureInfo.InvariantCulture));
                l.Add("driverowheight=" + DriveRowHeight.ToString(CultureInfo.InvariantCulture));
                l.Add("order=" + string.Join(",", Order.ToArray()));

                l.Add("showseconds=" + B(ShowSeconds));
                l.Add("clocksize=" + F(ClockSize));
                l.Add("datesize=" + F(DateSize));
                l.Add("daysize=" + F(DaySize));
                l.Add("datebold=" + B(DateBold));
                l.Add("daybold=" + B(DayBold));
                l.Add("clockcolor=" + Hex(ClockColor));
                l.Add("datecolor=" + Hex(DateColor));
                l.Add("daycolor=" + Hex(DayColor));

                l.Add("drivelabelsize=" + F(DriveLabelSize));
                l.Add("drivelabelbold=" + B(DriveLabelBold));
                l.Add("listsize=" + F(ListSize));
                l.Add("ipsize=" + F(IpSize));
                l.Add("nettotalssize=" + F(NetTotalsSize));

                l.Add("netunitbytes=" + B(NetUnitBytes));
                l.Add("externalip=" + B(ExternalIpEnabled));

                l.Add("tempfahrenheit=" + B(TempFahrenheit));
                l.Add("showcputemp=" + B(ShowCpuTemp));
                l.Add("showgputemp=" + B(ShowGpuTemp));
                l.Add("temphotc=" + D(TempHotC));
                l.Add("accuratecputempdriver=" + B(AccurateCpuTempDriver));

                l.Add("cpucolor=" + Hex(CpuColor));
                l.Add("gpucolor=" + Hex(GpuColor));
                l.Add("memcolor=" + Hex(MemColor));
                l.Add("diskcolor=" + Hex(DiskColor));
                l.Add("netdowncolor=" + Hex(NetDownColor));
                l.Add("netupcolor=" + Hex(NetUpColor));

                l.Add("cpumax=" + D(CpuMax));
                l.Add("gpumax=" + D(GpuMax));
                l.Add("memmax=" + D(MemMax));
                l.Add("diskmax=" + D(DiskMax));
                l.Add("netmax=" + D(NetMax));

                l.Add("cpualert=" + D(CpuAlert));
                l.Add("gpualert=" + D(GpuAlert));
                l.Add("memalert=" + D(MemAlert));
                l.Add("diskalert=" + D(DiskAlert));
                l.Add("netalert=" + D(NetAlert));

                l.Add("alwaysontop=" + B(AlwaysOnTop));
                l.Add("locked=" + B(Locked));
                l.Add("refreshms=" + RefreshMs.ToString(CultureInfo.InvariantCulture));
                l.Add("debuglog=" + B(DebugLog));
                l.Add("iplanrefreshsec=" + IpLanRefreshSec.ToString(CultureInfo.InvariantCulture));
                l.Add("ipwanrefreshsec=" + IpWanRefreshSec.ToString(CultureInfo.InvariantCulture));

                foreach (string key in AllSections)
                    l.Add("show_" + key + "=" + B(GetShow(key)));

                if (includePosition)
                    foreach (KeyValuePair<string, int[]> e in Positions)
                        l.Add("pos." + e.Key + "=" +
                              e.Value[0].ToString(CultureInfo.InvariantCulture) + "," +
                              e.Value[1].ToString(CultureInfo.InvariantCulture));

                File.WriteAllLines(path, l.ToArray());
            }
            catch { /* ignore */ }
        }

        // Keep only known keys (in saved order), then append any known keys that are missing
        // (forward-compatibility when new sections are added).
        private static List<string> NormalizeOrder(string raw)
        {
            List<string> result = new List<string>();
            if (!string.IsNullOrEmpty(raw))
            {
                foreach (string part in raw.Split(','))
                {
                    string k = part.Trim().ToLowerInvariant();
                    if (Array.IndexOf(AllSections, k) >= 0 && !result.Contains(k))
                        result.Add(k);
                }
            }
            foreach (string k in AllSections)
                if (!result.Contains(k)) result.Add(k);
            return result;
        }

        // ---- formatting helpers ----
        private static string B(bool v) { return v ? "true" : "false"; }
        private static string F(float v) { return v.ToString("0.#", CultureInfo.InvariantCulture); }
        private static string D(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }
        private static string Hex(Color c) { return string.Format(CultureInfo.InvariantCulture, "{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B); }

        // ---- parse helpers ----
        private static string GetString(Dictionary<string, string> kv, string key, string def)
        {
            string v;
            return kv.TryGetValue(key, out v) ? v : def;
        }

        private static int GetInt(Dictionary<string, string> kv, string key, int def)
        {
            bool had = false;
            return GetInt(kv, key, def, ref had);
        }

        private static int GetInt(Dictionary<string, string> kv, string key, int def, ref bool found)
        {
            string v; int r;
            if (kv.TryGetValue(key, out v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r))
            {
                found = true;
                return r;
            }
            return def;
        }

        private static double GetDouble(Dictionary<string, string> kv, string key, double def)
        {
            string v; double r;
            if (kv.TryGetValue(key, out v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out r))
                return r;
            return def;
        }

        private static float GetFloat(Dictionary<string, string> kv, string key, float def)
        {
            string v; float r;
            if (kv.TryGetValue(key, out v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out r))
                return r;
            return def;
        }

        private static bool GetBool(Dictionary<string, string> kv, string key, bool def)
        {
            string v;
            if (!kv.TryGetValue(key, out v)) return def;
            v = v.Trim().ToLowerInvariant();
            if (v == "true" || v == "1" || v == "yes") return true;
            if (v == "false" || v == "0" || v == "no") return false;
            return def;
        }

        private static Color GetColor(Dictionary<string, string> kv, string key, Color def)
        {
            string v;
            if (!kv.TryGetValue(key, out v)) return def;
            v = v.Trim().TrimStart('#');
            int rgb;
            if (v.Length == 6 && int.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb))
                return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
            return def;
        }
    }
}
