using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace LoadView
{
    // Shared plumbing between the overlay (medium integrity) and the elevated driver helper:
    // provisioning (download + verify + extract) LibreHardwareMonitor, and the small files they
    // use to talk. Everything lives under %APPDATA%\LoadView so the same-user elevated helper sees
    // it. Downloading only on opt-in keeps the shipped LoadView.exe free of any driver bytes.
    internal static class TempIpc
    {
        // Pinned LibreHardwareMonitor release (net472 build) and its verified SHA-256. This zip
        // bundles LibreHardwareMonitorLib.dll together with all of its runtime dependencies.
        public const string LhmVersion = "v0.9.6";
        private const string LhmUrl =
            "https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases/download/v0.9.6/LibreHardwareMonitor.zip";
        private const string LhmSha256 =
            "086D9F1B5A99E643EDC2CFAAAC16051685B551E4C5AC0B32A57C58C0E529C001";

        private static string Dir()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LoadView");
        }
        public static string LibDir() { return Path.Combine(Dir(), "lib"); }
        public static string LhmDllPath() { return Path.Combine(LibDir(), "LibreHardwareMonitorLib.dll"); }
        private static string OkMarker() { return Path.Combine(LibDir(), "lhm-" + LhmVersion + ".ok"); }
        private static string CpuTempPath() { return Path.Combine(Dir(), "cputemp"); }
        private static string HeartbeatPath() { return Path.Combine(Dir(), "helper.run"); }
        public static string HelperLogPath() { return Path.Combine(Dir(), "helper.log"); }

        // ---- library provisioning (runs in the elevated helper) ----

        public static bool LibraryReady()
        {
            try { return File.Exists(OkMarker()) && File.Exists(LhmDllPath()); }
            catch { return false; }
        }

        // Download the pinned zip, verify its SHA-256, and extract the DLLs into LibDir. Returns
        // false (and logs) on any failure so the caller can fall back gracefully.
        public static bool EnsureLibrary()
        {
            try
            {
                if (LibraryReady()) return true;
                string lib = LibDir();
                if (!Directory.Exists(lib)) Directory.CreateDirectory(lib);

                string zip = Path.Combine(lib, "lhm-" + LhmVersion + ".zip");
                try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; } catch { }
                using (WebClient wc = new WebClient())
                    wc.DownloadFile(LhmUrl, zip);

                if (!HashEquals(zip, LhmSha256))
                {
                    HelperLog("download hash mismatch -- aborting");
                    try { File.Delete(zip); } catch { }
                    return false;
                }

                // Extract only the top-level *.dll entries (skip exe/pdb/xml/localized resources).
                using (ZipArchive za = ZipFile.OpenRead(zip))
                {
                    foreach (ZipArchiveEntry e in za.Entries)
                    {
                        if (e.FullName.IndexOf('/') >= 0 || e.FullName.IndexOf('\\') >= 0) continue;
                        if (!e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                        e.ExtractToFile(Path.Combine(lib, e.Name), true);
                    }
                }
                try { File.Delete(zip); } catch { }

                if (!File.Exists(LhmDllPath())) { HelperLog("extract missing LHM dll"); return false; }
                File.WriteAllText(OkMarker(), LhmSha256);
                return true;
            }
            catch (Exception ex) { HelperLog("EnsureLibrary: " + ex.Message); return false; }
        }

        private static bool HashEquals(string file, string expectedHex)
        {
            using (FileStream fs = File.OpenRead(file))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(fs);
                StringBuilder sb = new StringBuilder(h.Length * 2);
                for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("X2", CultureInfo.InvariantCulture));
                return string.Equals(sb.ToString(), expectedHex, StringComparison.OrdinalIgnoreCase);
            }
        }

        // ---- CPU temperature channel (helper writes, overlay reads) ----

        public static void WriteCpuTemp(double celsius)
        {
            try { File.WriteAllText(CpuTempPath(), celsius.ToString("0.0", CultureInfo.InvariantCulture)); }
            catch { }
        }

        public static bool TryReadCpuTemp(out double celsius, out DateTime whenUtc)
        {
            celsius = 0; whenUtc = DateTime.MinValue;
            try
            {
                string p = CpuTempPath();
                if (!File.Exists(p)) return false;
                whenUtc = File.GetLastWriteTimeUtc(p);
                double c;
                if (double.TryParse(File.ReadAllText(p).Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out c) && c > -50 && c < 150)
                { celsius = c; return true; }
            }
            catch { }
            return false;
        }

        // ---- heartbeat (overlay writes while enabled, helper watches) ----

        public static void WriteHeartbeat()
        {
            try { File.WriteAllText(HeartbeatPath(), DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture)); }
            catch { }
        }

        public static void ClearHeartbeat()
        {
            try { if (File.Exists(HeartbeatPath())) File.Delete(HeartbeatPath()); } catch { }
        }

        public static bool HeartbeatFresh(double maxAgeSec)
        {
            try
            {
                string p = HeartbeatPath();
                if (!File.Exists(p)) return false;
                return (DateTime.UtcNow - File.GetLastWriteTimeUtc(p)).TotalSeconds < maxAgeSec;
            }
            catch { return false; }
        }

        public static void HelperLog(string msg)
        {
            try
            {
                File.AppendAllText(HelperLogPath(),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + msg + "\r\n");
            }
            catch { }
        }
    }
}
