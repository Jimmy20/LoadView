using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace LoadView
{
    // One-time elevated provisioning for the accurate CPU temperature, plus the on-demand trigger
    // the unprivileged overlay uses afterwards.
    //
    // The reader must run as SYSTEM. LibreHardwareMonitor gets the CPU die temperature by opening
    // \\?\GLOBALROOT\Device\PawnIO, and that device's DACL is Administrators/SYSTEM only — from a
    // medium token the open fails with ERROR_ACCESS_DENIED and LHM simply reports no sensor. A task
    // registered with RunLevel=HighestAvailable does NOT fix that for a standard user: with no
    // elevated token to pick up, "highest available" is the ordinary medium one. Registering the
    // task's principal as SYSTEM works regardless of whether the logged-on user is an admin, and
    // still costs only the single UAC prompt at setup time.
    //
    // Because a SYSTEM task that any interactive user can start is a privilege-escalation primitive
    // if it runs code from a place a non-admin can write, setup stages the exe and the sensor
    // library into an admin-only directory and hardens every folder it touches (see SecureDir).
    internal static class PawnIoSetup
    {
        public const string TaskName = "LoadView CPU Temp Helper";

        // Pinned PawnIO release — not /releases/latest/, so the bytes we vouch for cannot change
        // underneath us. LibreHardwareMonitor 0.9.6 ships PawnIO modules 2.2 and version-checks the
        // installed driver, so 2.2.0 is also the minimum that works.
        private const string PawnIoVersion = "2.2.0";
        private const string MinDriverVersion = "2.2.0";
        private const string InstallerUrl =
            "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe";
        // PawnIO's author. Requiring the signer's subject — not merely "some valid signature" —
        // means a validly signed installer from anybody else is refused.
        private const string InstallerSubject = "CN=namazso.eu";

        // SYSTEM + Administrators full control; INTERACTIVE (S-1-5-4) read + execute, i.e. "may
        // start this task, may not modify it". D:P blocks the Tasks folder's inheritable ACEs, and
        // the explicit owner matters because an owner implicitly holds WRITE_DAC.
        //
        // Deliberately not a specific user's SID: nothing has to be passed from the unprivileged
        // overlay into the elevated setup (which would be an SDDL-injection channel), it needs no
        // merging when a second user enables the feature, and INTERACTIVE is tighter than Users
        // since it is absent from service and network logons.
        private const string TaskSddl = "O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FRFX;;;IU)";
        private const int TaskDontAddPrincipalAce = 0x10;

        // ---- detection ----

        // PawnIO's installed version, "" when absent. This is the same registry value LHM reads.
        private static string DriverVersionString()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("DisplayVersion");
                        if (v != null) return v.ToString();
                    }
                }
            }
            catch { }
            return "";
        }

        public static bool DriverInstalled()
        {
            Version have = ParseVersion(DriverVersionString());
            Version need = ParseVersion(MinDriverVersion);
            return have != null && need != null && have >= need;
        }

        private static Version ParseVersion(string s)
        {
            try { return string.IsNullOrEmpty(s) ? null : new Version(s.Trim()); }
            catch { return null; }
        }

        // Everything provisioned, current, and pointing where we expect. Ordered cheapest-first so
        // the COM round-trip only happens once the on-disk state already looks right.
        public static bool Ready()
        {
            if (!TempIpc.SetupStampCurrent()) return false;
            if (!File.Exists(TempIpc.StagedExePath())) return false;
            if (!TempIpc.LibraryReady()) return false;
            if (!DriverInstalled()) return false;
            return TaskIsCurrent();
        }

        // The task exists, runs as SYSTEM, and launches the staged (admin-only) exe. Also returns
        // false for the pre-2.10 per-user task, so those installs get re-provisioned.
        private static bool TaskIsCurrent()
        {
            string xml = TaskXml();
            if (xml == null) return false;
            if (xml.IndexOf("S-1-5-18", StringComparison.OrdinalIgnoreCase) < 0) return false;
            return xml.IndexOf(TempIpc.StagedExePath(), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Start the elevated helper through the task — no UAC, because the task carries its own
        // privileges and INTERACTIVE has execute rights on it.
        public static void RunHelperTask()
        {
            Run(SysPath("schtasks.exe"), "/Run /TN \"" + TaskName + "\"", 15000);
        }

        // ---- one-time elevated setup ("LoadView.exe --temp-setup [lhmZipPath]") ----

        public static void RunSetup(string[] argv)
        {
            string suppliedZip = argv != null && argv.Length > 2 ? argv[2] : null;
            TempIpc.HelperLog("setup: start (running as " + CurrentIdentity() + ")");
            try
            {
                if (!HardenDirs()) { TempIpc.HelperLog("setup: ABORT: could not secure the folders"); return; }

                StopHelper();

                if (!StageBinary()) { TempIpc.HelperLog("setup: ABORT: could not stage the exe"); return; }

                if (!EnsureDriver()) { TempIpc.HelperLog("setup: ABORT: PawnIO not installed"); return; }

                if (!TempIpc.EnsureLibraryElevated(suppliedZip))
                { TempIpc.HelperLog("setup: ABORT: sensor library not available"); return; }

                DeleteTask();   // drop any older task (incl. the pre-2.10 per-user one)
                if (!CreateSystemTask()) { TempIpc.HelperLog("setup: ABORT: task not created"); return; }
                if (!SetTaskSecurity()) TempIpc.HelperLog("setup: WARNING: task security not applied");

                TempIpc.WriteSetupStamp();
                Verify();
            }
            catch (Exception ex) { TempIpc.HelperLog("setup fatal: " + ex.GetType().Name + " " + ex.Message); }
            TempIpc.HelperLog("setup: done (ready=" + Ready() + ")");
        }

        // Undo everything setup created ("LoadView.exe --temp-remove", elevated). PawnIO itself is
        // left alone on purpose: other tools (LibreHardwareMonitor, HWiNFO) may be using it.
        public static void RunRemove()
        {
            TempIpc.HelperLog("remove: start");
            try
            {
                StopHelper();
                DeleteTask();
                Delete(TempIpc.ProgDir());
                Delete(TempIpc.SharedDir());
                Log.Write("temp-remove: task + folders removed (PawnIO left installed)");
            }
            catch (Exception ex) { Log.Write("temp-remove", ex); }
        }

        private static void Delete(string dir)
        {
            try
            {
                if (SecureDir.IsReparsePoint(dir)) return;   // never follow a junction while deleting
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch (Exception ex) { Log.Write("temp-remove delete " + dir, ex); }
        }

        // ---- setup steps ----

        // Create/repair every directory with a protected DACL *before* anything is written into it.
        private static bool HardenDirs()
        {
            bool ok = true;
            ok &= SecureDir.Harden(TempIpc.ProgDir(), false);     // Users: read + execute
            ok &= SecureDir.Harden(TempIpc.LibDir(), false);
            ok &= SecureDir.Harden(TempIpc.StageDir(), false);
            ok &= SecureDir.Harden(TempIpc.SharedDir(), false);
            ok &= SecureDir.Harden(TempIpc.OutDir(), false);      // SYSTEM writes, users read
            ok &= SecureDir.Harden(TempIpc.InDir(), true);        // users write the heartbeat here
            TempIpc.HelperLog("setup: folders secured = " + ok);
            return ok;
        }

        // Copy ourselves into the admin-only folder and let the task run *that* copy. The source is
        // this process's own loaded image, which cannot be swapped while it is running, and never a
        // path handed to us.
        private static bool StageBinary()
        {
            try
            {
                string src = Process.GetCurrentProcess().MainModule.FileName;
                string dst = TempIpc.StagedExePath();
                File.Copy(src, dst, true);
                TempIpc.HelperLog("setup: staged " + dst);
                return File.Exists(dst);
            }
            catch (Exception ex) { TempIpc.HelperLog("setup: stage exe: " + ex.Message); return false; }
        }

        private static bool EnsureDriver()
        {
            if (DriverInstalled())
            {
                TempIpc.HelperLog("setup: PawnIO already installed (" + DriverVersionString() + ")");
                return true;
            }

            // Download into the hardened staging folder, then verify and run it *there*: with the
            // old %TEMP% location the file sat in a user-writable directory between the signature
            // check and the launch, so it could be swapped in between.
            string dst = Path.Combine(TempIpc.StageDir(), "PawnIO_setup.exe");
            try
            {
                try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; } catch { }
                using (WebClient wc = new WebClient())
                {
                    try
                    {
                        wc.UseDefaultCredentials = true;
                        IWebProxy px = WebRequest.GetSystemWebProxy();
                        px.Credentials = CredentialCache.DefaultCredentials;
                        wc.Proxy = px;
                    }
                    catch { }
                    wc.DownloadFile(InstallerUrl, dst);
                }
            }
            catch (Exception ex)
            { TempIpc.HelperLog("setup: installer download failed: " + ex.Message); return false; }

            try { TempIpc.HelperLog("setup: installer sha256 = " + TempIpc.Sha256Hex(dst)); } catch { }

            if (!SignatureTrusted(dst))
            {
                TempIpc.HelperLog("setup: installer signature not accepted -> not running it");
                return false;
            }

            TempIpc.HelperLog("setup: installing PawnIO " + PawnIoVersion);
            Run(dst, "-install -silent", 180000);
            for (int i = 0; i < 20 && !DriverInstalled(); i++) System.Threading.Thread.Sleep(500);
            TempIpc.HelperLog("setup: PawnIO install "
                + (DriverInstalled() ? "OK (" + DriverVersionString() + ")" : "FAILED"));
            try { File.Delete(dst); } catch { }
            return DriverInstalled();
        }

        // Windows must call the signature valid (this is a real WinVerifyTrust via
        // Get-AuthenticodeSignature) AND the signer must be PawnIO's author.
        private static bool SignatureTrusted(string file)
        {
            try
            {
                string ps = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "
                    + "\"$s = Get-AuthenticodeSignature -LiteralPath '" + file.Replace("'", "''") + "';"
                    + " $s.Status.ToString() + '|' + $s.SignerCertificate.Subject"
                    + " + '|' + $s.SignerCertificate.Thumbprint\"";
                string outp = Capture(SysPath("WindowsPowerShell\\v1.0\\powershell.exe"), ps, 60000);
                if (outp == null) { TempIpc.HelperLog("setup: signature check produced no output"); return false; }
                outp = outp.Trim();
                TempIpc.HelperLog("setup: installer signature = " + outp);

                bool valid = outp.StartsWith("Valid", StringComparison.OrdinalIgnoreCase);
                bool signer = outp.IndexOf(InstallerSubject, StringComparison.OrdinalIgnoreCase) >= 0;
                if (!valid) TempIpc.HelperLog("setup: signature status is not Valid");
                if (!signer) TempIpc.HelperLog("setup: signer is not " + InstallerSubject);
                return valid && signer;
            }
            catch (Exception ex) { TempIpc.HelperLog("setup: signature check failed: " + ex.Message); return false; }
        }

        // Register the on-demand task. Everything the reader needs is explicit here rather than
        // left to Task Scheduler's defaults, so a policy-flipped default can't quietly disable it.
        private static bool CreateSystemTask()
        {
            string exe = TempIpc.StagedExePath();
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <RegistrationInfo>\r\n" +
                "    <Description>LoadView CPU temperature reader (runs on demand, reads the CPU sensor).</Description>\r\n" +
                "  </RegistrationInfo>\r\n" +
                // S-1-5-18 = LOCAL SYSTEM. Note there is deliberately no <LogonType>: the task XML
                // schema's logonType enum only accepts S4U / Password / InteractiveToken /
                // InteractiveTokenOrPassword, so "ServiceAccount" (a COM-only TASK_LOGON_TYPE value)
                // is rejected outright with "(8,33):LogonType:ServiceAccount". For a service account
                // the logon type is implied by the SID — this is exactly how Windows writes its own
                // SYSTEM tasks.
                "  <Principals><Principal id=\"Author\">\r\n" +
                "    <UserId>S-1-5-18</UserId>\r\n" +
                "    <RunLevel>HighestAvailable</RunLevel>\r\n" +
                "  </Principal></Principals>\r\n" +
                "  <Settings>\r\n" +
                "    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
                "    <Enabled>true</Enabled>\r\n" +
                "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
                "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
                "    <AllowHardTerminate>true</AllowHardTerminate>\r\n" +
                "    <StartWhenAvailable>false</StartWhenAvailable>\r\n" +
                "    <WakeToRun>false</WakeToRun>\r\n" +
                "    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\r\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
                "    <RunOnlyIfIdle>false</RunOnlyIfIdle>\r\n" +
                "    <IdleSettings>\r\n" +
                "      <StopOnIdleEnd>false</StopOnIdleEnd>\r\n" +
                "      <RestartOnIdle>false</RestartOnIdle>\r\n" +
                "    </IdleSettings>\r\n" +
                "    <Priority>7</Priority>\r\n" +
                "  </Settings>\r\n" +
                "  <Actions Context=\"Author\"><Exec>\r\n" +
                "    <Command>" + XmlEscape(exe) + "</Command>\r\n" +
                "    <Arguments>--temp-helper</Arguments>\r\n" +
                "    <WorkingDirectory>" + XmlEscape(TempIpc.ProgDir()) + "</WorkingDirectory>\r\n" +
                "  </Exec></Actions>\r\n" +
                "</Task>\r\n";

            string xmlPath = Path.Combine(TempIpc.StageDir(), "task.xml");
            try
            {
                File.WriteAllText(xmlPath, xml, Encoding.Unicode);
                int rc = Run(SysPath("schtasks.exe"),
                    "/Create /TN \"" + TaskName + "\" /XML \"" + xmlPath + "\" /F", 30000);
                TempIpc.HelperLog("setup: schtasks /Create rc=" + rc);
            }
            catch (Exception ex) { TempIpc.HelperLog("setup: create task: " + ex.Message); }
            try { File.Delete(xmlPath); } catch { }
            return TaskIsCurrent();
        }

        public static void DeleteTask()
        {
            Run(SysPath("schtasks.exe"), "/Delete /TN \"" + TaskName + "\" /F", 15000, false);
        }

        private static void StopHelper()
        {
            Run(SysPath("schtasks.exe"), "/End /TN \"" + TaskName + "\"", 15000, false);
            System.Threading.Thread.Sleep(1500);   // let the old helper release the staged exe
        }

        // Log what we actually ended up with, so a refusal by policy is diagnosable from the log
        // rather than looking like a silent failure.
        private static void Verify()
        {
            TempIpc.HelperLog("setup: task security = " + (TaskSecurityDescriptor() ?? "(unreadable)"));
            TempIpc.HelperLog("setup: admin-only check: prog=" + SecureDir.IsAdminOnly(TempIpc.ProgDir())
                + " lib=" + SecureDir.IsAdminOnly(TempIpc.LibDir())
                + " out=" + SecureDir.IsAdminOnly(TempIpc.OutDir()));
        }

        // ---- Task Scheduler COM (late-bound, same style as Startup.CreateShortcut) ----

        private static object Inv(object o, string member, params object[] args)
        {
            return o.GetType().InvokeMember(member, BindingFlags.InvokeMethod, null, o, args);
        }

        private static object GetProp(object o, string member)
        {
            return o.GetType().InvokeMember(member, BindingFlags.GetProperty, null, o, null);
        }

        private static void Release(object o)
        {
            try { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); } catch { }
        }

        // Hands the registered task to 'work'; returns work's result, or null on any COM failure.
        private static object WithTask(Func<object, object> work)
        {
            Type t = Type.GetTypeFromProgID("Schedule.Service");
            if (t == null) return null;
            object svc = null, folder = null, task = null;
            try
            {
                svc = Activator.CreateInstance(t);
                Inv(svc, "Connect");
                folder = Inv(svc, "GetFolder", "\\");
                task = Inv(folder, "GetTask", TaskName);
                return work(task);
            }
            catch { return null; }
            finally { Release(task); Release(folder); Release(svc); }
        }

        private static string TaskXml()
        {
            object r = WithTask(delegate(object task) { return GetProp(task, "Xml"); });
            return r == null ? null : r.ToString();
        }

        private static string TaskSecurityDescriptor()
        {
            // 7 = OWNER | GROUP | DACL
            object r = WithTask(delegate(object task)
            { return Inv(task, "GetSecurityDescriptor", 7); });
            return r == null ? null : r.ToString();
        }

        // schtasks cannot set a task's security descriptor, so this is the one place we need the
        // COM API: grant INTERACTIVE the right to *start* the SYSTEM task (and nothing more).
        private static bool SetTaskSecurity()
        {
            object r = WithTask(delegate(object task)
            {
                Inv(task, "SetSecurityDescriptor", TaskSddl, TaskDontAddPrincipalAce);
                return "ok";
            });
            return r != null;
        }

        // ---- process helpers ----

        // Absolute paths only. A bare "schtasks" would let CreateProcess find a planted
        // schtasks.exe in this process's image directory first — and this process is elevated.
        private static string SysPath(string relative)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), relative);
        }

        private static string CurrentIdentity()
        {
            try { return System.Security.Principal.WindowsIdentity.GetCurrent().Name; }
            catch { return "?"; }
        }

        // logFailure=false for calls that are expected to fail on a first install (ending or deleting
        // a task that doesn't exist yet), so the log doesn't carry misleading "ERROR" lines.
        private static int Run(string file, string args, int timeoutMs, bool logFailure = true)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(file, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                // Never inherit a user-writable working directory into an elevated child.
                psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
                Process p = Process.Start(psi);
                if (p == null) return -1;
                string so = p.StandardOutput.ReadToEnd();
                string se = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return -2; }

                // Log why it failed — without this a non-zero exit code is a dead end to diagnose.
                if (p.ExitCode != 0 && logFailure)
                {
                    string msg = (se + " " + so).Replace("\r", " ").Replace("\n", " ").Trim();
                    if (msg.Length > 300) msg = msg.Substring(0, 300);
                    if (msg.Length > 0)
                        TempIpc.HelperLog("  " + Path.GetFileName(file) + " rc=" + p.ExitCode + ": " + msg);
                }
                return p.ExitCode;
            }
            catch (Exception ex)
            {
                TempIpc.HelperLog("run '" + Path.GetFileName(file) + "' failed: " + ex.Message);
                return -3;
            }
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
                psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
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
