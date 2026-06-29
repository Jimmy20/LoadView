using System;
using System.Globalization;
using System.IO;

namespace LoadView
{
    // Tiny INI persisted to %APPDATA%\LoadView\settings.ini. Failures are swallowed:
    // a missing/corrupt file just means "use defaults".
    internal sealed class Settings
    {
        public bool HasPosition;
        public int X;
        public int Y;
        public double Opacity = 0.9;

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
            bool haveX = false, haveY = false;
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
                    if (key == "x") { int v; if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) { s.X = v; haveX = true; } }
                    else if (key == "y") { int v; if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) { s.Y = v; haveY = true; } }
                    else if (key == "opacity") { double d; if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) { s.Opacity = d; } }
                }
            }
            catch { /* ignore corrupt settings */ }

            s.HasPosition = haveX && haveY;
            return s;
        }

        public void Save()
        {
            try
            {
                string path = FilePath();
                string dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string[] lines = new string[]
                {
                    "x=" + X.ToString(CultureInfo.InvariantCulture),
                    "y=" + Y.ToString(CultureInfo.InvariantCulture),
                    "opacity=" + Opacity.ToString("0.00", CultureInfo.InvariantCulture)
                };
                File.WriteAllLines(path, lines);
            }
            catch { /* ignore */ }
        }
    }
}
