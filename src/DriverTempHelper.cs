using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace LoadView
{
    // Runs as SYSTEM via the scheduled task ("LoadView.exe --temp-helper"), started on demand by
    // the overlay. Reflection-loads the LibreHardwareMonitor library that the elevated setup staged
    // (so the normal build needs no extra reference), reads the true CPU package temperature —
    // which requires SYSTEM/admin, since that goes through the PawnIO device — and publishes it to
    // the unprivileged overlay through TempIpc. Exits once the overlay's heartbeat goes stale.
    //
    // This process is fully privileged, so it treats everything around it as untrusted: it verifies
    // that the folders it loads code from are admin-only, never downloads anything itself, and never
    // writes into the folder the unprivileged side can write.
    internal static class DriverTempHelper
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetDefaultDllDirectories(uint directoryFlags);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectory(string lpPathName);

        private const uint LoadLibrarySearchSystem32 = 0x00000800;
        private const uint LoadLibrarySearchDefaultDirs = 0x00001000;

        // No process-wide mutex on purpose: a Global\ name is creatable by any interactive user, who
        // could either squat it (leaving this helper to exit instantly, killing the feature) or
        // create it with a DACL denying SYSTEM. Single-instancing is the task's
        // MultipleInstancesPolicy=IgnoreNew instead.
        public static void Run(string[] argv)
        {
            try { RunCore(argv); }
            catch (Exception ex)
            { TempIpc.HelperLog("helper fatal: " + ex.GetType().Name + " " + ex.Message); }
        }

        private static void RunCore(string[] argv)
        {
            TempIpc.HelperLog("helper start (running as " + CurrentIdentity() + ")");

            // Drop the current directory (and anything else ahead of System32) from the native DLL
            // search path before loading any assembly.
            HardenDllSearch();

            string libDir = TempIpc.LibDir();
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;

            // Fail closed rather than trust that setup got the ACLs right: if a non-admin can write
            // where we load code from, this privileged process must not load it.
            if (!SecureDir.IsAdminOnly(exeDir))
            { TempIpc.HelperLog("helper: exe folder is not admin-only -> refusing to run"); return; }
            if (!SecureDir.IsAdminOnly(libDir))
            { TempIpc.HelperLog("helper: lib folder is not admin-only -> refusing to run"); return; }

            // Provisioning belongs to the elevated setup. A SYSTEM process must not download a zip
            // and load the DLLs it extracts.
            if (!TempIpc.LibraryReady())
            { TempIpc.HelperLog("helper: sensor library missing -> run setup again"); return; }

            ResolveEventHandler resolver = delegate(object s, ResolveEventArgs e)
            {
                try
                {
                    string name = new AssemblyName(e.Name).Name + ".dll";
                    string path = Path.Combine(libDir, name);
                    return File.Exists(path) ? Assembly.LoadFrom(path) : null;
                }
                catch { return null; }
            };
            AppDomain.CurrentDomain.AssemblyResolve += resolver;

            object computer = null;
            Type computerType = null;
            try
            {
                Assembly lhm = Assembly.LoadFrom(TempIpc.LhmDllPath());
                computerType = lhm.GetType("LibreHardwareMonitor.Hardware.Computer");
                if (computerType == null) { TempIpc.HelperLog("Computer type not found"); return; }

                computer = Activator.CreateInstance(computerType);
                computerType.GetProperty("IsCpuEnabled").SetValue(computer, true, null);
                computerType.GetMethod("Open", Type.EmptyTypes).Invoke(computer, null);
                TempIpc.HelperLog("LHM opened");

                PropertyInfo hardwareProp = computerType.GetProperty("Hardware");
                int idle = 0;
                bool logged = false;

                while (true)
                {
                    if (!TempIpc.HeartbeatFresh(8.0)) { TempIpc.HelperLog("heartbeat stale -> exit"); break; }

                    double c;
                    if (TryReadCpu(hardwareProp, computer, out c))
                    {
                        TempIpc.WriteCpuTemp(c);
                        if (!logged)
                        {
                            TempIpc.HelperLog("first CPU temp " + c.ToString("0.0", CultureInfo.InvariantCulture));
                            logged = true;
                        }
                        idle = 0;
                    }
                    else if (++idle == 3)
                    {
                        // Reaching this as SYSTEM means the sensor really isn't exposed — as a
                        // medium-integrity process it just meant the PawnIO device was denied.
                        TempIpc.HelperLog("no CPU temperature sensor found (identity "
                            + CurrentIdentity() + ", PawnIO device open failed?)");
                    }

                    for (int i = 0; i < 20; i++) { Thread.Sleep(100); if (!TempIpc.HeartbeatFresh(8.0)) break; }
                }
            }
            finally
            {
                try
                {
                    if (computer != null && computerType != null)
                        computerType.GetMethod("Close", Type.EmptyTypes).Invoke(computer, null);
                }
                catch { }
                AppDomain.CurrentDomain.AssemblyResolve -= resolver;
                TempIpc.HelperLog("helper stopped");
            }
        }

        private static void HardenDllSearch()
        {
            try
            {
                if (SetDefaultDllDirectories(LoadLibrarySearchSystem32 | LoadLibrarySearchDefaultDirs))
                    return;
            }
            catch { }
            try { SetDllDirectory(""); } catch { }
        }

        private static string CurrentIdentity()
        {
            try { return System.Security.Principal.WindowsIdentity.GetCurrent().Name; }
            catch { return "?"; }
        }

        // Prefer the CPU package sensor (Intel "CPU Package" / AMD "Core (Tctl/Tdie)"), else the
        // hottest CPU temperature sensor.
        private static bool TryReadCpu(PropertyInfo hardwareProp, object computer, out double celsius)
        {
            celsius = 0;
            double pkg = double.NaN, best = double.MinValue;
            IEnumerable hardware = (IEnumerable)hardwareProp.GetValue(computer, null);
            foreach (object hw in hardware)
            {
                Type ht = hw.GetType();
                object hwType = ht.GetProperty("HardwareType").GetValue(hw, null);
                if (hwType == null || hwType.ToString() != "Cpu") continue;
                ht.GetMethod("Update", Type.EmptyTypes).Invoke(hw, null);

                IEnumerable sensors = (IEnumerable)ht.GetProperty("Sensors").GetValue(hw, null);
                foreach (object se in sensors)
                {
                    Type st = se.GetType();
                    object stype = st.GetProperty("SensorType").GetValue(se, null);
                    if (stype == null || stype.ToString() != "Temperature") continue;
                    object val = st.GetProperty("Value").GetValue(se, null); // float?
                    if (val == null) continue;
                    double v = Convert.ToDouble(val, CultureInfo.InvariantCulture);
                    if (v <= 0 || v >= 150) continue;

                    string name = "";
                    object no = st.GetProperty("Name").GetValue(se, null);
                    if (no != null) name = no.ToString();
                    if (name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("Tctl", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("Tdie", StringComparison.OrdinalIgnoreCase) >= 0)
                        pkg = v;
                    if (v > best) best = v;
                }
            }
            if (!double.IsNaN(pkg)) { celsius = pkg; return true; }
            if (best > double.MinValue) { celsius = best; return true; }
            return false;
        }
    }
}
