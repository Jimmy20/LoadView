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
        // PawnIO's author. Requiring the signer — not merely "some valid signature" — means a
        // validly signed installer from anybody else is refused.
        //
        // The subject is compared as a whole RDN, never as a substring: "CN=namazso.eu" occurs
        // inside "CN=namazso.eu.example.org" too, and a certificate for such a domain is obtainable
        // by whoever controls it. The thumbprint (SHA-1 of the certificate, read off the real
        // installer on 2026-08-12) is the exact identity and carries no parsing ambiguity.
        private const string InstallerCn = "namazso.eu";
        private const string InstallerThumbprint = "F380DCC9F706E2756A5047B832FFE719E1BC35F5";
        // SHA-256 of PawnIO_setup.exe 2.2.0 as served by the pinned release URL, measured on
        // 2026-08-12. Checked in addition to the signature, so the exact bytes are pinned too.
        private const string InstallerSha256 =
            "1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032";

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
        //
        // Called repeatedly while the feature is on, so most calls hit MultipleInstancesPolicy
        // IgnoreNew and are refused. Task Scheduler records that as Last Result 0x800710E0
        // ("the operator or administrator has refused the request"), which looks like a failure in
        // the Task Scheduler UI but is the expected steady state: the helper is already running.
        public static void RunHelperTask()
        {
            Run(SysPath("schtasks.exe"), "/Run /TN \"" + TaskName + "\"", 15000);
        }

        // ---- one-time elevated setup ("LoadView.exe --temp-setup [lhmZipPath]") ----

        public static void RunSetup(string[] argv)
        {
            string suppliedZip = argv != null && argv.Length > 2 ? argv[2] : null;
            try
            {
                // Wipe, harden, and only then log. Order matters and each step is load-bearing:
                //
                //  * Wipe first, because fixing a folder's DACL leaves the files already in it and
                //    the planter keeps ownership (hence WRITE_DAC) of them. A planted cputemp.tmp
                //    or helper.log could be a hard link, and SYSTEM's write would follow it.
                //  * Log last, because helper.log is itself inside the tree being wiped — so the
                //    wipe reports into a buffer and we flush it once the tree is ours.
                StringBuilder notes = new StringBuilder();
                SecureDir.DeleteTree(TempIpc.SharedDir(), notes);
                bool secured = HardenDirs();
                int raced = SecureDir.WipeFiles(TempIpc.OutDir(), notes)
                          + SecureDir.WipeFiles(TempIpc.StageDir(), notes);

                TempIpc.HelperLog("setup: start (running as " + CurrentIdentity() + ")");
                foreach (string n in notes.ToString().Split('\n'))
                    if (n.Length > 0) TempIpc.HelperLog("  " + n);
                TempIpc.HelperLog("setup: folders secured = " + secured
                    + (raced > 0 ? " (wiped " + raced + " file(s) that appeared during hardening)" : ""));
                if (!secured) { TempIpc.HelperLog("setup: ABORT: could not secure the folders"); return; }

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
            ok &= SecureDir.Harden(TempIpc.ProgDir(), UsersAccess.ReadExecute);
            ok &= SecureDir.Harden(TempIpc.LibDir(), UsersAccess.ReadExecute);
            // Nothing unprivileged reads stage\ — only this elevated setup writes there. Keeping
            // users out matters because whatever path is handed to --temp-setup gets copied in:
            // with Users:R that turned an elevated run into a way to expose any file it can read.
            ok &= SecureDir.Harden(TempIpc.StageDir(), UsersAccess.None);
            ok &= SecureDir.Harden(TempIpc.SharedDir(), UsersAccess.ReadExecute);
            ok &= SecureDir.Harden(TempIpc.OutDir(), UsersAccess.ReadExecute);   // SYSTEM writes, users read
            ok &= SecureDir.Harden(TempIpc.InDir(), UsersAccess.Modify);         // the heartbeat lands here
            return ok;   // logged by the caller, which can only write once these exist
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
                // OR it in: this property is process-global, so assigning would drop TLS 1.3 and
                // whatever else the system default enables.
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                using (WebClient wc = new WebClient())
                {
                    try
                    {
                        // Credentials go on the PROXY only. Setting them on the client makes the
                        // request answer a 401 Negotiate/NTLM challenge from the *target* too, which
                        // hands a Net-NTLM hash to whatever answers for that host.
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

            // Pin the exact bytes as well as the signer. The URL is version-pinned, so a release
            // asset that no longer hashes to this was replaced after the fact — refuse rather than
            // fall back to trusting the signature alone. Both gates must pass.
            string hash = null;
            try { hash = TempIpc.Sha256Hex(dst); } catch (Exception ex) { TempIpc.HelperLog("setup: hashing failed: " + ex.Message); }
            TempIpc.HelperLog("setup: installer sha256 = " + (hash ?? "(unreadable)"));
            if (hash == null || !string.Equals(hash, InstallerSha256, StringComparison.OrdinalIgnoreCase))
            {
                TempIpc.HelperLog("setup: installer hash does not match the pinned "
                    + InstallerSha256 + " -> not running it");
                return false;
            }

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

                // Parse the three fields instead of searching the whole line: a match anywhere used
                // to satisfy the signer test, including inside the thumbprint or a longer domain.
                string[] parts = outp.Split('|');
                if (parts.Length < 3)
                { TempIpc.HelperLog("setup: signature output not understood -> refusing"); return false; }

                string status = parts[0].Trim();
                string subject = parts[1].Trim();
                string thumb = parts[2].Trim().Replace(" ", "");

                bool valid = string.Equals(status, "Valid", StringComparison.OrdinalIgnoreCase);
                bool tp = string.Equals(thumb, InstallerThumbprint, StringComparison.OrdinalIgnoreCase);
                bool cn = SubjectHasCn(subject, InstallerCn);

                if (!valid) TempIpc.HelperLog("setup: signature status is '" + status + "', not Valid");
                if (!tp) TempIpc.HelperLog("setup: certificate thumbprint is not " + InstallerThumbprint);
                if (!cn) TempIpc.HelperLog("setup: signer CN is not " + InstallerCn);
                return valid && tp && cn;
            }
            catch (Exception ex) { TempIpc.HelperLog("setup: signature check failed: " + ex.Message); return false; }
        }

        // True when the certificate subject carries exactly CN=<cn> as one of its comma-separated
        // RDNs — so "CN=namazso.eu.example.org" and "O=CN=namazso.eu" both fail, while the real
        // "E=admin@namazso.eu, CN=namazso.eu, O=namazso, L=Debrecen, C=HU" passes.
        private static bool SubjectHasCn(string subject, string cn)
        {
            if (string.IsNullOrEmpty(subject)) return false;
            string[] rdns = subject.Split(',');
            for (int i = 0; i < rdns.Length; i++)
            {
                string rdn = rdns[i].Trim().Trim('"');
                if (string.Equals(rdn, "CN=" + cn, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
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
                // Both pipes must be drained concurrently. Reading stdout to completion and only
                // then stderr is a deadlock: a child that fills the ~4 KB stderr buffer blocks
                // writing, never closes stdout, and ReadToEnd() never returns — so WaitForExit is
                // never reached and the timeout below cannot fire. That would hang an elevated
                // setup indefinitely on a prompt the user already approved.
                StringBuilder outBuf = new StringBuilder(), errBuf = new StringBuilder();
                Process p = new Process();
                p.StartInfo = psi;
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                { if (e.Data != null) outBuf.Append(e.Data).Append(' '); };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                { if (e.Data != null) errBuf.Append(e.Data).Append(' '); };
                if (!p.Start()) return -1;
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return -2; }
                p.WaitForExit();   // the timed overload doesn't wait for the async readers to finish

                // Log why it failed — without this a non-zero exit code is a dead end to diagnose.
                if (p.ExitCode != 0 && logFailure)
                {
                    string msg = (errBuf.ToString() + " " + outBuf.ToString()).Replace("\r", " ").Replace("\n", " ").Trim();
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

                // Same reason as Run(): drain both pipes asynchronously, or the timeout is a lie.
                StringBuilder outBuf = new StringBuilder();
                Process p = new Process();
                p.StartInfo = psi;
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                { if (e.Data != null) outBuf.Append(e.Data); };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { };
                if (!p.Start()) return null;
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return null; }
                p.WaitForExit();   // flush the async readers before the caller parses the output
                return outBuf.ToString();
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
