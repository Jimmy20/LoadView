using System;
using System.IO;
using System.Text;

namespace LoadView
{
    // Opt-in diagnostic log (off unless enabled in Settings). Writes to
    // %APPDATA%\LoadView\loadview.log; self-truncates so it can't grow unbounded.
    internal static class Log
    {
        private static readonly object _lock = new object();
        public static bool Enabled;
        private static string _file;

        private static string FilePath()
        {
            if (_file == null)
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LoadView");
                _file = Path.Combine(dir, "loadview.log");
            }
            return _file;
        }

        public static void Write(string message)
        {
            if (!Enabled) return;
            try
            {
                lock (_lock)
                {
                    string p = FilePath();
                    string dir = Path.GetDirectoryName(p);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    try { FileInfo fi = new FileInfo(p); if (fi.Exists && fi.Length > 512 * 1024) File.Delete(p); }
                    catch { }
                    File.AppendAllText(p,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch { }
        }

        public static void Write(string context, Exception ex)
        {
            if (!Enabled) return;
            Write(context + ": " + (ex == null ? "?" : ex.GetType().Name + " " + ex.Message));
        }
    }
}
