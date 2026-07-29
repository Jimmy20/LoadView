using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.Win32;

namespace LoadView
{
    // Provisions the free, open-source, digitally-signed PawnIO driver so the accurate CPU
    // temperature works even with Windows Memory Integrity (HVCI) on — where the old WinRing0
    // driver is blocked. Reading via PawnIO still needs an elevated process, so a one-time
    // elevated "setup" (single UAC) both installs PawnIO and registers a Task Scheduler task
    // (HighestAvailable) that can later launch the helper elevated WITHOUT any further UAC.
    //
    // We never bundle PawnIO: we download its official signed installer on demand and verify the
    // Authenticode signature before running it. PawnIO is GPL-2.0 and distributed by its author.
    internal static class PawnIoSetup
    {
        public const string TaskName = "LoadView CPU Temp Helper";
        private const string InstallerUrl =
            "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";

        // ---- detection ----

        public static bool DriverInstalled()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\PawnIO"))
                    if (k != null) return true;
            }
            catch { }
            try
            {
                string sys = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "PawnIO.sys");
                if (File.Exists(sys)) return true;
            }
            catch { }
            return false;
        }

        public static bool TaskExists()
        {
            return Run("schtasks", "/Query /TN \"" + TaskName + "\"", 15000, false) == 0;
        }

        // Everything is in place to run the helper silently (elevated, no UAC).
        public static bool Ready() { return DriverInstalled() && TaskExists(); }

        // Launch the elevated helper via the scheduled task — no UAC prompt.
        public static void RunHelperTask()
        {
            Run("schtasks", "/Run /TN \"" + TaskName + "\"", 15000, false);
        }

        // ---- one-time elevated setup (runs as "LoadView.exe --temp-setup") ----

        public static void RunSetup()
        {
            TempIpc.HelperLog("setup: start");
            try
            {
                if (!DriverInstalled())
                {
                    bool ok = InstallDriver();
                    TempIpc.HelperLog("setup: PawnIO install " + (ok ? "OK" : "FAILED"));
                }
                else TempIpc.HelperLog("setup: PawnIO already installed");

                CreateTask();
                TempIpc.HelperLog("setup: scheduled task " + (TaskExists() ? "created" : "MISSING"));
            }
            catch (Exception ex) { TempIpc.HelperLog("setup fatal: " + ex.Message); }
            TempIpc.HelperLog("setup: done (driver=" + DriverInstalled() + " task=" + TaskExists() + ")");
        }

        private static bool InstallDriver()
        {
            string dst = Path.Combine(Path.GetTempPath(), "PawnIO_setup.exe");
            try
            {
                try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; } catch { }
                using (WebClient wc = new WebClient()) wc.DownloadFile(InstallerUrl, dst);
            }
            catch (Exception ex) { TempIpc.HelperLog("setup: installer download failed: " + ex.Message); return false; }

            if (!SignatureValid(dst))
            {
                TempIpc.HelperLog("setup: installer signature NOT valid -> aborting install");
                return false;
            }

            // PawnIO_setup.exe is an NSIS installer; /S runs it silently. If a build ignores it,
            // the installer UI shows briefly (still launched by us) — either way we re-check after.
            Run(dst, "/S", 180000, false);
            for (int i = 0; i < 20 && !DriverInstalled(); i++) System.Threading.Thread.Sleep(500);
            return DriverInstalled();
        }

        // Verify the downloaded installer carries a valid, trusted Authenticode signature before
        // we run it (we only run it if Windows says the signature Status is "Valid").
        private static bool SignatureValid(string file)
        {
            try
            {
                string ps = "-NoProfile -ExecutionPolicy Bypass -Command "
                    + "\"(Get-AuthenticodeSignature -LiteralPath '" + file.Replace("'", "''") + "').Status\"";
                string outp = Capture("powershell", ps, 30000);
                bool valid = outp != null && outp.IndexOf("Valid", StringComparison.OrdinalIgnoreCase) >= 0;
                TempIpc.HelperLog("setup: installer signature = " + (outp == null ? "(none)" : outp.Trim()));
                return valid;
            }
            catch (Exception ex) { TempIpc.HelperLog("setup: signature check failed: " + ex.Message); return false; }
        }

        // Register an on-demand task that runs the helper elevated (HighestAvailable) in the
        // user's session, without a UAC prompt when triggered.
        private static void CreateTask()
        {
            string exe = ExePath();
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <RegistrationInfo><Description>LoadView elevated CPU temperature helper</Description></RegistrationInfo>\r\n" +
                "  <Principals><Principal id=\"Author\">\r\n" +
                "    <LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel>\r\n" +
                "  </Principal></Principals>\r\n" +
                "  <Settings>\r\n" +
                "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
                "    <AllowHardTerminate>true</AllowHardTerminate>\r\n" +
                "    <StartWhenAvailable>false</StartWhenAvailable>\r\n" +
                "    <Enabled>true</Enabled>\r\n" +
                "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
                "    <Priority>7</Priority>\r\n" +
                "  </Settings>\r\n" +
                "  <Actions Context=\"Author\"><Exec>\r\n" +
                "    <Command>" + XmlEscape(exe) + "</Command>\r\n" +
                "    <Arguments>--temp-helper</Arguments>\r\n" +
                "  </Exec></Actions>\r\n" +
                "</Task>\r\n";

            string xmlPath = Path.Combine(Path.GetTempPath(), "loadview_task.xml");
            File.WriteAllText(xmlPath, xml, Encoding.Unicode);
            Run("schtasks", "/Create /TN \"" + TaskName + "\" /XML \"" + xmlPath + "\" /F", 20000, false);
            try { File.Delete(xmlPath); } catch { }
        }

        // Remove the task (used if the user turns the feature off permanently — optional).
        public static void DeleteTask()
        {
            Run("schtasks", "/Delete /TN \"" + TaskName + "\" /F", 15000, false);
        }

        // ---- process helpers ----

        private static string ExePath()
        {
            try
            {
                Assembly a = Assembly.GetEntryAssembly();
                if (a != null && !string.IsNullOrEmpty(a.Location)) return a.Location;
            }
            catch { }
            return Process.GetCurrentProcess().MainModule.FileName;
        }

        private static int Run(string file, string args, int timeoutMs, bool shell)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(file, args);
                psi.UseShellExecute = shell;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                if (!shell) { psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; }
                Process p = Process.Start(psi);
                if (p == null) return -1;
                if (!shell) { p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); }
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return -2; }
                return p.ExitCode;
            }
            catch (Exception ex) { TempIpc.HelperLog("run '" + file + "' failed: " + ex.Message); return -3; }
        }

        private static string Capture(string file, string args, int timeoutMs)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(file, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                Process p = Process.Start(psi);
                if (p == null) return null;
                string outp = p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return null; }
                return outp;
            }
            catch { return null; }
        }

        private static string XmlEscape(string s)
        {
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace("\"", "&quot;").Replace("'", "&apos;");
        }
    }
}
