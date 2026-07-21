using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Threading;

namespace LoadView
{
    // Runs elevated: "LoadView.exe --temp-helper <overlayPid>". Loads the downloaded
    // LibreHardwareMonitor library via reflection (no compile-time dependency, so the normal build
    // needs nothing extra), reads the true CPU package temperature through its kernel driver, and
    // publishes it to the non-elevated overlay via TempIpc. Exits when the overlay stops (its
    // heartbeat goes stale) or the parent process exits.
    internal static class DriverTempHelper
    {
        public static void Run(string[] argv)
        {
            bool createdNew;
            using (Mutex m = new Mutex(true, @"Global\LoadView_TempHelper", out createdNew))
            {
                if (!createdNew) return; // one helper at a time
                try { RunCore(argv); }
                catch (Exception ex) { TempIpc.HelperLog("helper fatal: " + ex.Message); }
            }
        }

        private static void RunCore(string[] argv)
        {
            int parentPid = -1;
            if (argv.Length > 2)
                int.TryParse(argv[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out parentPid);
            TempIpc.HelperLog("helper start (parent=" + parentPid + ")");

            if (!TempIpc.EnsureLibrary()) { TempIpc.HelperLog("library not available"); return; }

            string libDir = TempIpc.LibDir();
            ResolveEventHandler resolver = delegate(object s, ResolveEventArgs e)
            {
                try
                {
                    string name = new AssemblyName(e.Name).Name + ".dll";
                    string path = System.IO.Path.Combine(libDir, name);
                    return System.IO.File.Exists(path) ? Assembly.LoadFrom(path) : null;
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
                    if (parentPid > 0 && !ProcessAlive(parentPid)) { TempIpc.HelperLog("parent gone -> exit"); break; }

                    double c;
                    if (TryReadCpu(hardwareProp, computer, out c))
                    {
                        TempIpc.WriteCpuTemp(c);
                        if (!logged) { TempIpc.HelperLog("first CPU temp " + c.ToString("0.0", CultureInfo.InvariantCulture)); logged = true; }
                        idle = 0;
                    }
                    else if (++idle == 3)
                    {
                        TempIpc.HelperLog("no CPU temperature sensor found");
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

        private static bool ProcessAlive(int pid)
        {
            try { System.Diagnostics.Process.GetProcessById(pid); return true; }
            catch { return false; }
        }
    }
}
