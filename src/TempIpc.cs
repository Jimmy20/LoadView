using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace LoadView
{
    // Shared plumbing between the overlay (per-user, medium integrity) and the CPU-temperature
    // helper, which runs as SYSTEM through a scheduled task.
    //
    // Why SYSTEM: LibreHardwareMonitor reads the CPU die temperature by opening
    // \\?\GLOBALROOT\Device\PawnIO, whose DACL is Administrators/SYSTEM only. From a medium
    // token that CreateFile returns ERROR_ACCESS_DENIED and LHM degrades silently — which is why
    // the temperature stayed blank for standard (non-admin) users, who have no elevated token for
    // Task Scheduler's "HighestAvailable" to pick up.
    //
    // Layout, and why it is split three ways:
    //   %ProgramFiles%\LoadView\      Users:RX  the exe copy the task runs + LibreHardwareMonitor.
    //                                          Admin-only because SYSTEM executes and loads it;
    //                                          also the path shape default AppLocker/WDAC allow.
    //   %ProgramData%\LoadView\out\   Users:R   SYSTEM writes (cputemp, helper.log), user reads.
    //   %ProgramData%\LoadView\in\    Users:M   user writes (helper.run); SYSTEM only ever *stats*
    //                                          it — never writing into a user-writable folder is
    //                                          what stops a planted hard link from becoming an
    //                                          arbitrary SYSTEM write.
    //   %APPDATA%\LoadView\                     unchanged per-user state (settings.ini, flags\).
    internal static class TempIpc
    {
        // Bumped whenever this on-disk layout changes, so an older setup is detected and redone.
        public const string SetupSchema = "2";

        // Pinned LibreHardwareMonitor release (net472 build) and its verified SHA-256. This zip
        // bundles LibreHardwareMonitorLib.dll together with all of its runtime dependencies.
        public const string LhmVersion = "v0.9.6";
        private const string LhmUrl =
            "https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases/download/v0.9.6/LibreHardwareMonitor.zip";
        private const string LhmSha256 =
            "086D9F1B5A99E643EDC2CFAAAC16051685B551E4C5AC0B32A57C58C0E529C001";
        // Sanity cap for a caller-supplied zip: the real one is ~1.5 MB, so anything near this is
        // already wrong and not worth hashing.
        private const long MaxLhmZipBytes = 64L * 1024 * 1024;

        // ---- paths: code (admin-only) ----

        public static string ProgDir()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LoadView");
        }
        public static string LibDir() { return Path.Combine(ProgDir(), "lib"); }
        public static string StageDir() { return Path.Combine(ProgDir(), "stage"); }
        public static string StagedExePath() { return Path.Combine(ProgDir(), "LoadView.exe"); }
        public static string LhmDllPath() { return Path.Combine(LibDir(), "LibreHardwareMonitorLib.dll"); }
        private static string OkMarker() { return Path.Combine(LibDir(), "lhm-" + LhmVersion + ".ok"); }
        private static string SetupStampPath() { return Path.Combine(ProgDir(), "setup.txt"); }

        // ---- paths: data channel, split by direction ----

        private static string SharedRoot()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LoadView");
        }
        public static string SharedDir() { return SharedRoot(); }
        public static string OutDir() { return Path.Combine(SharedRoot(), "out"); }
        public static string InDir() { return Path.Combine(SharedRoot(), "in"); }

        private static string CpuTempPath() { return Path.Combine(OutDir(), "cputemp"); }
        private static string SensorsPath() { return Path.Combine(OutDir(), "sensors"); }
        private static string HeartbeatPath() { return Path.Combine(InDir(), "helper.run"); }
        // Presence = "also probe the chipset and fans". In in\, the one folder the unprivileged
        // overlay may write; the SYSTEM helper only ever checks whether it exists.
        public static string WideSensorsFlagPath() { return Path.Combine(InDir(), "widesensors"); }
        public static string HelperLogPath() { return Path.Combine(OutDir(), "helper.log"); }

        // ---- setup stamp: what the elevated setup provisioned, so we can spot a stale install ----

        public static string ExpectedStamp()
        {
            return SetupSchema + "|" + AppInfo.Version + "|" + LhmVersion;
        }

        public static void WriteSetupStamp()
        {
            try { File.WriteAllText(SetupStampPath(), ExpectedStamp()); }
            catch (Exception ex) { HelperLog("write setup stamp: " + ex.Message); }
        }

        public static bool SetupStampCurrent()
        {
            try
            {
                if (!File.Exists(SetupStampPath())) return false;
                return File.ReadAllText(SetupStampPath()).Trim() == ExpectedStamp();
            }
            catch { return false; }
        }

        // ---- library provisioning (elevated setup only — never the SYSTEM helper) ----

        public static bool LibraryReady()
        {
            try { return File.Exists(OkMarker()) && File.Exists(LhmDllPath()); }
            catch { return false; }
        }

        // Put the pinned LibreHardwareMonitor build into the admin-only lib dir. The zip is either
        // downloaded here or handed over by the overlay (see OverlayForm.PreDownloadLhm: the user's
        // own process is the one that has the corporate proxy's credentials). Either way it is
        // copied into the hardened staging dir FIRST and the *copy* is hashed, so a user-supplied
        // path can't be swapped between the check and the extract.
        public static bool EnsureLibraryElevated(string suppliedZip)
        {
            try
            {
                if (LibraryReady()) return true;
                string lib = LibDir(), stage = StageDir();
                if (!Directory.Exists(lib) || !Directory.Exists(stage))
                { HelperLog("lib/stage dir missing -- harden step failed?"); return false; }

                string zip = Path.Combine(stage, "lhm-" + LhmVersion + ".zip");
                try { if (File.Exists(zip)) File.Delete(zip); } catch { }

                if (SuppliedZipAcceptable(suppliedZip))
                {
                    File.Copy(suppliedZip, zip, true);
                    HelperLog("lib: using the zip the overlay downloaded");
                }
                else
                {
                    try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                    using (WebClient wc = new WebClient())
                    {
                        try
                        {
                            // Proxy credentials only — see the note in PawnIoSetup.EnsureDriver.
                            IWebProxy px = WebRequest.GetSystemWebProxy();
                            px.Credentials = CredentialCache.DefaultCredentials;
                            wc.Proxy = px;
                        }
                        catch { }
                        wc.DownloadFile(LhmUrl, zip);
                    }
                }

                if (!HashEquals(zip, LhmSha256))
                {
                    HelperLog("lib: zip hash mismatch -- aborting");
                    try { File.Delete(zip); } catch { }
                    return false;
                }

                // Extract only the top-level *.dll entries (skip exe/pdb/xml/localized resources).
                // Entries containing a separator are skipped, which also rules out zip-slip.
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

                if (!File.Exists(LhmDllPath())) { HelperLog("lib: extract missing LHM dll"); return false; }
                File.WriteAllText(OkMarker(), LhmSha256);
                return true;
            }
            catch (Exception ex) { HelperLog("EnsureLibraryElevated: " + ex.Message); return false; }
        }

        // Decide whether the path handed to "--temp-setup <path>" may be copied into the staging
        // folder — and hash it BEFORE copying, not after.
        //
        // This runs elevated with a caller-supplied path, so it is the one place where an
        // unprivileged (or merely careless) caller picks a file a privileged process will read. With
        // the copy happening first, naming something like C:\Windows\System32\config\SAM put its
        // bytes into stage\ until the hash check removed them again. Checking the source first means
        // a file that isn't the pinned release is never copied anywhere; the copy is then re-hashed
        // by the caller, which closes the swap-in-between window.
        private static bool SuppliedZipAcceptable(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                FileInfo fi = new FileInfo(path);
                if (!fi.Exists) return false;
                // A reparse point could point anywhere, including somewhere only SYSTEM can read.
                if ((fi.Attributes & FileAttributes.ReparsePoint) != 0)
                { HelperLog("lib: supplied zip is a reparse point -> ignoring it"); return false; }
                if (fi.Length > MaxLhmZipBytes)
                { HelperLog("lib: supplied zip is " + fi.Length + " bytes -> ignoring it"); return false; }
                if (!HashEquals(path, LhmSha256))
                { HelperLog("lib: supplied zip is not the pinned release -> ignoring it"); return false; }
                return true;
            }
            catch (Exception ex) { HelperLog("lib: supplied zip rejected: " + ex.Message); return false; }
        }

        // Fetch the pinned zip in the *user's* context. An elevated process — possibly running under
        // a different admin account — may not have the credentials a corporate proxy wants, whereas
        // this one demonstrably does. The elevated side re-hashes whatever it is handed, so this is a
        // convenience, not a trust decision.
        public static string DownloadLhmZipAsUser()
        {
            try
            {
                string p = Path.Combine(Path.GetTempPath(), "LoadView-lhm-" + LhmVersion + ".zip");
                if (File.Exists(p) && HashEquals(p, LhmSha256)) return p;

                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                using (WebClient wc = new WebClient())
                {
                    try
                    {
                        IWebProxy px = WebRequest.GetSystemWebProxy();
                        px.Credentials = CredentialCache.DefaultCredentials;
                        wc.Proxy = px;
                    }
                    catch { }
                    wc.DownloadFile(LhmUrl, p);
                }
                return HashEquals(p, LhmSha256) ? p : null;
            }
            catch (Exception ex) { Log.Write("LHM pre-download", ex); return null; }
        }

        // Drop the pre-2.10 per-user copies. These were written by the overlay's own account, so the
        // overlay is the right place to remove them — an elevated setup can't reliably find another
        // user's profile. settings.ini and flags\ are per-user state and stay.
        public static void CleanLegacyUserFiles()
        {
            try
            {
                string old = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LoadView");
                string lib = Path.Combine(old, "lib");
                if (Directory.Exists(lib)) Directory.Delete(lib, true);
                string[] stale = new string[] { "cputemp", "helper.run", "helper.log" };
                for (int i = 0; i < stale.Length; i++)
                {
                    string p = Path.Combine(old, stale[i]);
                    if (File.Exists(p)) File.Delete(p);
                }
            }
            catch (Exception ex) { Log.Write("legacy temp-file cleanup", ex); }
        }

        public static string Sha256Hex(string file)
        {
            using (FileStream fs = File.OpenRead(file))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(fs);
                StringBuilder sb = new StringBuilder(h.Length * 2);
                for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("X2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        private static bool HashEquals(string file, string expectedHex)
        {
            return string.Equals(Sha256Hex(file), expectedHex, StringComparison.OrdinalIgnoreCase);
        }

        // ---- CPU temperature channel (SYSTEM writes to out\, the overlay reads) ----

        public static void WriteCpuTemp(double celsius)
        {
            WriteAtomic(CpuTempPath(), celsius.ToString("0.0", CultureInfo.InvariantCulture));
        }

        // Write a file SYSTEM owns, without ever opening an entry that is already there.
        //
        // out\ is admin-only, so this is belt-and-braces behind setup's wipe — but a file planted
        // before setup ran could be a hard link, and a plain write would follow it and modify the
        // link's target with SYSTEM's rights (measured in v2.11.0: the write went straight through).
        // Delete, then CreateNew: an entry we did not just create fails the write instead of
        // redirecting it. The rename at the end means a reader never sees a half-written file.
        private static void WriteAtomic(string dst, string text)
        {
            try
            {
                string tmp = dst + ".tmp";
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                using (FileStream fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] b = Encoding.UTF8.GetBytes(text);
                    fs.Write(b, 0, b.Length);
                }
                if (File.Exists(dst)) File.Replace(tmp, dst, null);
                else File.Move(tmp, dst);
            }
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

        // ---- multi-sensor channel: chipset temperatures and fan speeds ----
        //
        // cputemp stays exactly as it was, so the shipped CPU-temperature path cannot regress if
        // anything here goes wrong. This file is additional.
        //
        // Format, one sensor per line after a version marker:
        //     v1
        //     t|/lpc/nct6797d/temperature/2|Chipset|54.0
        //     f|/lpc/nct6797d/fan/1|CPU Fan|2412
        // Written by SYSTEM into a Users:Read folder, so it is trusted input — but it is still parsed
        // defensively (size cap, line cap, id charset, value range), because a parser that trusts its
        // input is a parser nobody can safely change later.
        private const int MaxSensorBytes = 64 * 1024;
        private const int MaxSensorLines = 200;

        public static void WriteSensors(SensorReading[] readings)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("v1\n");
            int n = 0;
            for (int i = 0; i < readings.Length && n < MaxSensorLines; i++)
            {
                SensorReading r = readings[i];
                if (string.IsNullOrEmpty(r.Id)) continue;
                sb.Append(r.Kind == SensorKind.Fan ? 'f' : 't').Append('|')
                  .Append(Clean(r.Id)).Append('|')
                  .Append(Clean(r.Label)).Append('|')
                  .Append(r.Value.ToString("0.0", CultureInfo.InvariantCulture)).Append('\n');
                n++;
            }
            WriteAtomic(SensorsPath(), sb.ToString());
        }

        public static SensorReading[] ReadSensors(out DateTime whenUtc)
        {
            whenUtc = DateTime.MinValue;
            try
            {
                string p = SensorsPath();
                if (!File.Exists(p)) return new SensorReading[0];
                FileInfo fi = new FileInfo(p);
                if (fi.Length > MaxSensorBytes) return new SensorReading[0];
                whenUtc = fi.LastWriteTimeUtc;

                string[] lines = File.ReadAllLines(p);
                if (lines.Length == 0 || lines[0].Trim() != "v1") return new SensorReading[0];

                List<SensorReading> list = new List<SensorReading>();
                for (int i = 1; i < lines.Length && list.Count < MaxSensorLines; i++)
                {
                    string[] f = lines[i].Split('|');
                    if (f.Length != 4) continue;
                    bool fan = f[0] == "f";
                    if (!fan && f[0] != "t") continue;
                    if (f[1].Length == 0 || f[1].Length > 120) continue;

                    double v;
                    if (!double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out v)) continue;
                    if (fan) { if (v < 0 || v > 20000) continue; }
                    else { if (v <= 0 || v >= 150) continue; }

                    SensorReading r;
                    r.Id = f[1];
                    r.Label = f[2].Length > 0 ? f[2] : f[1];
                    r.Detail = "";
                    r.Kind = fan ? SensorKind.Fan : SensorKind.Temperature;
                    r.Value = v;
                    list.Add(r);
                }
                return list.ToArray();
            }
            catch { whenUtc = DateTime.MinValue; return new SensorReading[0]; }
        }

        // Keep the separator and newlines out of a field, so one odd sensor name cannot corrupt the
        // whole file.
        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length && sb.Length < 120; i++)
            {
                char c = s[i];
                if (c == '|' || c == '\r' || c == '\n') { sb.Append(' '); continue; }
                if (c < ' ') continue;
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        // ---- heartbeat (the overlay writes to in\ while enabled; SYSTEM only stats it) ----

        public static void WriteHeartbeat()
        {
            try
            {
                string dir = InDir();
                if (!Directory.Exists(dir)) return;   // provisioned by the elevated setup
                File.WriteAllText(HeartbeatPath(),
                    DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
            }
            catch { }
        }

        // Ask the helper for (or stop asking for) chipset and fan probing. Takes effect when the
        // helper next starts, which is why the caller restarts it.
        public static void SetWideSensors(bool on)
        {
            try
            {
                string p = WideSensorsFlagPath();
                if (on)
                {
                    if (!Directory.Exists(InDir())) return;
                    if (!File.Exists(p)) File.WriteAllText(p, "1");
                }
                else if (File.Exists(p)) File.Delete(p);
            }
            catch { }
        }

        public static void ClearHeartbeat()
        {
            try { if (File.Exists(HeartbeatPath())) File.Delete(HeartbeatPath()); } catch { }
        }

        // Metadata-only read: never opens the file, so a hard link planted in the user-writable
        // in\ folder gains nothing.
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

        // ---- diagnostics ----

        // Written by the elevated setup and the SYSTEM helper. The unprivileged overlay can only
        // read out\, so its own calls fall back to the opt-in per-user debug log.
        //
        // Deliberately never creates the directory. Users may create subfolders under
        // C:\ProgramData and CREATOR OWNER gets full control there, so a non-elevated caller
        // creating out\ would leave a user-owned folder with no hardened DACL — the exact starting
        // state SecureDir.Harden exists to defeat. Only the elevated setup makes these folders (via
        // Harden), which keeps "the feature off touches nothing under C:\ProgramData" a property of
        // the code rather than of which log lines happen to be reachable today.
        public static void HelperLog(string msg)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                + "  " + msg + "\r\n";
            try
            {
                if (!Directory.Exists(OutDir())) { Log.Write("temp: " + msg); return; }
                // Self-truncate like Log.Write: this file lives in out\, which is read-only for
                // users, so nobody but an administrator could ever clean it up by hand.
                try
                {
                    FileInfo fi = new FileInfo(HelperLogPath());
                    if (fi.Exists && fi.Length > 256 * 1024) File.Delete(HelperLogPath());
                }
                catch { }
                File.AppendAllText(HelperLogPath(), line);
            }
            catch { Log.Write("temp: " + msg); }
        }
    }
}
