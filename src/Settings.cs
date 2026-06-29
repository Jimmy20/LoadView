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
        // window / position
        public bool HasPosition;
        public int X;
        public int Y;
        public double Opacity = 0.9;

        // layout
        public int Width = 300;
        public int GraphHeight = 58;
        public int DriveRowHeight = 30;

        // clock / date / weekday
        public bool ShowSeconds = true;
        public float ClockSize = 20f;
        public float DateSize = 13f;
        public float DaySize = 10f;
        public Color ClockColor = Color.FromArgb(232, 232, 237);
        public Color DateColor = Color.FromArgb(232, 232, 237);
        public Color DayColor = Color.FromArgb(150, 150, 158);

        // behavior
        public bool AlwaysOnTop = true;
        public bool Locked;

        // section visibility
        public bool ShowClock = true;
        public bool ShowCpu = true;
        public bool ShowGpu = true;
        public bool ShowMem = true;
        public bool ShowDisk = true;
        public bool ShowNet = true;
        public bool ShowDrives = true;
        public bool ShowFooter = true;

        public Settings Clone()
        {
            return (Settings)MemberwiseClone();
        }

        private static string FilePath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LoadView");
            return Path.Combine(dir, "settings.ini");
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            Dictionary<string, string> kv = new Dictionary<string, string>();
            try
            {
                string path = FilePath();
                if (!File.Exists(path)) return s;
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

            s.ShowSeconds = GetBool(kv, "showseconds", s.ShowSeconds);
            s.ClockSize = GetFloat(kv, "clocksize", s.ClockSize);
            s.DateSize = GetFloat(kv, "datesize", s.DateSize);
            s.DaySize = GetFloat(kv, "daysize", s.DaySize);
            s.ClockColor = GetColor(kv, "clockcolor", s.ClockColor);
            s.DateColor = GetColor(kv, "datecolor", s.DateColor);
            s.DayColor = GetColor(kv, "daycolor", s.DayColor);

            s.AlwaysOnTop = GetBool(kv, "alwaysontop", s.AlwaysOnTop);
            s.Locked = GetBool(kv, "locked", s.Locked);

            s.ShowClock = GetBool(kv, "showclock", s.ShowClock);
            s.ShowCpu = GetBool(kv, "showcpu", s.ShowCpu);
            s.ShowGpu = GetBool(kv, "showgpu", s.ShowGpu);
            s.ShowMem = GetBool(kv, "showmem", s.ShowMem);
            s.ShowDisk = GetBool(kv, "showdisk", s.ShowDisk);
            s.ShowNet = GetBool(kv, "shownet", s.ShowNet);
            s.ShowDrives = GetBool(kv, "showdrives", s.ShowDrives);
            s.ShowFooter = GetBool(kv, "showfooter", s.ShowFooter);

            return s;
        }

        public void Save()
        {
            try
            {
                string path = FilePath();
                string dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                List<string> l = new List<string>();
                l.Add("x=" + X.ToString(CultureInfo.InvariantCulture));
                l.Add("y=" + Y.ToString(CultureInfo.InvariantCulture));
                l.Add("opacity=" + Opacity.ToString("0.00", CultureInfo.InvariantCulture));
                l.Add("width=" + Width.ToString(CultureInfo.InvariantCulture));
                l.Add("graphheight=" + GraphHeight.ToString(CultureInfo.InvariantCulture));
                l.Add("driverowheight=" + DriveRowHeight.ToString(CultureInfo.InvariantCulture));
                l.Add("showseconds=" + (ShowSeconds ? "true" : "false"));
                l.Add("clocksize=" + ClockSize.ToString("0.#", CultureInfo.InvariantCulture));
                l.Add("datesize=" + DateSize.ToString("0.#", CultureInfo.InvariantCulture));
                l.Add("daysize=" + DaySize.ToString("0.#", CultureInfo.InvariantCulture));
                l.Add("clockcolor=" + Hex(ClockColor));
                l.Add("datecolor=" + Hex(DateColor));
                l.Add("daycolor=" + Hex(DayColor));
                l.Add("alwaysontop=" + (AlwaysOnTop ? "true" : "false"));
                l.Add("locked=" + (Locked ? "true" : "false"));
                l.Add("showclock=" + (ShowClock ? "true" : "false"));
                l.Add("showcpu=" + (ShowCpu ? "true" : "false"));
                l.Add("showgpu=" + (ShowGpu ? "true" : "false"));
                l.Add("showmem=" + (ShowMem ? "true" : "false"));
                l.Add("showdisk=" + (ShowDisk ? "true" : "false"));
                l.Add("shownet=" + (ShowNet ? "true" : "false"));
                l.Add("showdrives=" + (ShowDrives ? "true" : "false"));
                l.Add("showfooter=" + (ShowFooter ? "true" : "false"));

                File.WriteAllLines(path, l.ToArray());
            }
            catch { /* ignore */ }
        }

        // ---- parse helpers ----

        private static int GetInt(Dictionary<string, string> kv, string key, int def)
        {
            bool had = false;
            return GetInt(kv, key, def, ref had);
        }

        private static int GetInt(Dictionary<string, string> kv, string key, int def, ref bool found)
        {
            string v;
            int r;
            if (kv.TryGetValue(key, out v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r))
            {
                found = true;
                return r;
            }
            return def;
        }

        private static double GetDouble(Dictionary<string, string> kv, string key, double def)
        {
            string v;
            double r;
            if (kv.TryGetValue(key, out v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out r))
                return r;
            return def;
        }

        private static float GetFloat(Dictionary<string, string> kv, string key, float def)
        {
            string v;
            float r;
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

        private static string Hex(Color c)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
        }
    }
}
