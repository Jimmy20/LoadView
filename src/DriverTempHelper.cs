using System;
using System.Collections;
using System.Collections.Generic;
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

                // Chipset temperatures and fan speeds live on the SuperIO / embedded controller, and
                // getting at them means letting the library probe those chips — which on some boards
                // involves writing to their registers. That is a bigger step than reading the CPU's
                // temperature register, so it has its own switch and is off unless asked for.
                bool wide = WideSensorsRequested();
                if (wide)
                {
                    // Motherboard and its controller only. Storage is deliberately NOT enabled: the
                    // overlay reads disk temperatures itself with no driver and no elevation, and
                    // measuring showed the library reporting the same NVMe drive again with worse
                    // labels ("Composite Te" against "C:"), so this would only duplicate tiles while
                    // giving a privileged process more to touch.
                    SetIfPresent(computerType, computer, "IsMotherboardEnabled");
                    SetIfPresent(computerType, computer, "IsControllerEnabled");
                }
                computerType.GetMethod("Open", Type.EmptyTypes).Invoke(computer, null);
                TempIpc.HelperLog("LHM opened" + (wide ? " (chipset + fan sensors enabled)" : ""));

                PropertyInfo hardwareProp = computerType.GetProperty("Hardware");
                int idle = 0;
                bool logged = false;

                while (true)
                {
                    if (!TempIpc.HeartbeatFresh(8.0)) { TempIpc.HelperLog("heartbeat stale -> exit"); break; }

                    // Chipset + fan readings, published alongside the CPU temperature.
                    if (wide)
                    {
                        try { TempIpc.WriteSensors(ReadWideSensors(hardwareProp, computer, !logged)); }
                        catch (Exception ex) { TempIpc.HelperLog("wide sensors: " + ex.Message); }
                    }

                    double c; bool fromPackage;
                    // Collect the sensor inventory on the first pass only — it is fixed for the
                    // life of the process, and it is what proves which sensor the value came from.
                    System.Text.StringBuilder inv = logged ? null : new System.Text.StringBuilder();
                    if (TryReadCpu(hardwareProp, computer, out c, out fromPackage, inv))
                    {
                        TempIpc.WriteCpuTemp(c);
                        if (!logged)
                        {
                            TempIpc.HelperLog("CPU temperature sensors: " + inv);
                            TempIpc.HelperLog("first CPU temp " + c.ToString("0.0", CultureInfo.InvariantCulture)
                                + (fromPackage ? " (package sensor)" : " (fallback: hottest sensor)"));
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

                    // 500 ms granularity, not 100: each check is two file-metadata calls, and this
                    // is a SYSTEM process polling for the whole session. Four checks per 2 s cycle
                    // still notices a closed overlay well inside the 8 s staleness budget.
                    for (int i = 0; i < 4; i++) { Thread.Sleep(500); if (!TempIpc.HeartbeatFresh(8.0)) break; }
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

        // The overlay asks for the wider probing by dropping a marker file into in\ — the only folder
        // it may write. A file rather than a command line because the scheduled task's arguments are
        // fixed (and must stay fixed: an argument a user could influence is an argument into a SYSTEM
        // process). Its presence is the whole message; nothing inside it is read.
        private static bool WideSensorsRequested()
        {
            try { return System.IO.File.Exists(TempIpc.WideSensorsFlagPath()); }
            catch { return false; }
        }

        private static void SetIfPresent(Type t, object o, string property)
        {
            try
            {
                PropertyInfo p = t.GetProperty(property);
                if (p != null && p.CanWrite) p.SetValue(o, true, null);
                else TempIpc.HelperLog("helper: " + property + " not available in this library version");
            }
            catch (Exception ex) { TempIpc.HelperLog("helper: " + property + ": " + ex.Message); }
        }

        // Every temperature and fan reading outside the CPU: motherboard / SuperIO / embedded
        // controller (chipset, VRM, board sensors) and storage. The CPU keeps its own dedicated
        // channel, so it is skipped here to avoid publishing it twice.
        private static SensorReading[] ReadWideSensors(PropertyInfo hardwareProp, object computer, bool logInventory)
        {
            List<SensorReading> list = new List<SensorReading>();
            System.Text.StringBuilder inv = logInventory ? new System.Text.StringBuilder() : null;

            IEnumerable hardware = (IEnumerable)hardwareProp.GetValue(computer, null);
            foreach (object hw in hardware)
            {
                Type ht = hw.GetType();
                object hwTypeObj = ht.GetProperty("HardwareType").GetValue(hw, null);
                string hwType = hwTypeObj == null ? "" : hwTypeObj.ToString();
                if (hwType == "Cpu") continue;

                ht.GetMethod("Update", Type.EmptyTypes).Invoke(hw, null);
                string hwName = "";
                object nm = ht.GetProperty("Name").GetValue(hw, null);
                if (nm != null) hwName = nm.ToString();

                Collect(hw, ht, hwType, hwName, list, inv);

                // Fans often hang off a sub-device (a Cooler under the motherboard), so one level of
                // SubHardware is walked too.
                try
                {
                    object subObj = ht.GetProperty("SubHardware") != null
                        ? ht.GetProperty("SubHardware").GetValue(hw, null) : null;
                    if (subObj is IEnumerable)
                    {
                        foreach (object sub in (IEnumerable)subObj)
                        {
                            Type st = sub.GetType();
                            st.GetMethod("Update", Type.EmptyTypes).Invoke(sub, null);
                            string subName = "";
                            object snm = st.GetProperty("Name").GetValue(sub, null);
                            if (snm != null) subName = snm.ToString();
                            Collect(sub, st, hwType, subName, list, inv);
                        }
                    }
                }
                catch (Exception ex) { TempIpc.HelperLog("subhardware: " + ex.Message); }
            }

            if (inv != null)
                TempIpc.HelperLog("chipset/fan sensors: " + (inv.Length == 0 ? "(none exposed)" : inv.ToString()));
            return list.ToArray();
        }

        private static void Collect(object hw, Type ht, string hwType, string hwName,
            List<SensorReading> list, System.Text.StringBuilder inv)
        {
            IEnumerable sensors = (IEnumerable)ht.GetProperty("Sensors").GetValue(hw, null);
            foreach (object se in sensors)
            {
                Type st = se.GetType();
                object stObj = st.GetProperty("SensorType").GetValue(se, null);
                string kind = stObj == null ? "" : stObj.ToString();
                if (kind != "Temperature" && kind != "Fan") continue;

                object val = st.GetProperty("Value").GetValue(se, null);
                if (val == null) continue;
                double v = Convert.ToDouble(val, CultureInfo.InvariantCulture);

                string name = "";
                object no = st.GetProperty("Name").GetValue(se, null);
                if (no != null) name = no.ToString();

                // Identifier is stable across reboots (e.g. /lpc/nct6797d/fan/1), which is what the
                // user's tile selection is stored against.
                string id = "";
                object idObj = st.GetProperty("Identifier") != null
                    ? st.GetProperty("Identifier").GetValue(se, null) : null;
                if (idObj != null) id = idObj.ToString();
                if (id.Length == 0) id = hwType + "/" + hwName + "/" + kind + "/" + name;

                if (inv != null)
                {
                    if (inv.Length > 0) inv.Append(", ");
                    inv.Append(hwType).Append(':').Append(name).Append('=')
                       .Append(v.ToString("0.#", CultureInfo.InvariantCulture));
                }

                if (kind == "Temperature")
                {
                    if (v <= 0 || v >= 150) continue;
                    // Not readings: thermal headroom, and the drive's own alarm thresholds. Measured
                    // on this machine, the library offered "Warning Temperature=82" and "Critical
                    // Temperature=84" as temperature sensors, which as a tile would look exactly like
                    // a component running at 84 degrees.
                    if (IsNotAReading(name)) continue;
                }
                else if (v < 0 || v > 20000) continue;

                SensorReading r;
                r.Id = id;
                r.Label = ShortLabel(name, hwName);
                r.Detail = hwName + " - " + name;
                r.Kind = kind == "Fan" ? SensorKind.Fan : SensorKind.Temperature;
                r.Value = v;
                list.Add(r);
            }
        }

        // A sensor whose name says it is a limit or a delta, not a current value.
        private static readonly string[] NotReadings =
            { "Distance", "Warning", "Critical", "Threshold", "Limit" };

        private static bool IsNotAReading(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            for (int i = 0; i < NotReadings.Length; i++)
                if (name.IndexOf(NotReadings[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // Tiles are small, so a sensor called "Temperature #2" on an "NCT6797D" is better shown as
        // something short and recognisable.
        private static string ShortLabel(string sensorName, string hardwareName)
        {
            string s = string.IsNullOrEmpty(sensorName) ? hardwareName : sensorName;
            if (s == null) s = "";
            s = s.Replace("Temperature", "Temp").Trim();
            if (s.Length > 12) s = s.Substring(0, 12).Trim();
            return s.Length == 0 ? "Sensor" : s;
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
        //
        // Pass an `inventory` to have every CPU temperature sensor recorded name=value. Because the
        // fallback silently reports the hottest sensor, a plausible-looking number is not evidence
        // that the package sensor was the one read — the inventory is what distinguishes the two,
        // and it is the only way to tell without a second sensor tool for comparison.
        private static bool TryReadCpu(PropertyInfo hardwareProp, object computer, out double celsius,
            out bool fromPackage, System.Text.StringBuilder inventory)
        {
            celsius = 0; fromPackage = false;
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
                    if (inventory != null)
                    {
                        if (inventory.Length > 0) inventory.Append(", ");
                        inventory.Append(name).Append('=')
                                 .Append(v.ToString("0.0", CultureInfo.InvariantCulture));
                    }
                    // "P-Core #1 Distance to TjMax" is thermal headroom, not a temperature, but LHM
                    // exposes it as a Temperature sensor (measured: 24-30 while the cores read
                    // 70-76). Left in the inventory above, kept out of the selection below, so the
                    // hottest-sensor fallback can never return a delta as if it were a reading.
                    if (name.IndexOf("Distance", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    if (name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("Tctl", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("Tdie", StringComparison.OrdinalIgnoreCase) >= 0)
                        pkg = v;
                    if (v > best) best = v;
                }
            }
            if (!double.IsNaN(pkg)) { celsius = pkg; fromPackage = true; return true; }
            if (best > double.MinValue) { celsius = best; return true; }
            return false;
        }
    }
}
