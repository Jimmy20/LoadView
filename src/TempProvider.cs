using System;
using System.Globalization;
using System.Management;   // requires /r:System.Management.dll
using System.Runtime.InteropServices;
using System.Threading;

namespace LoadView
{
    // Best-effort temperatures, read on a background thread and cached so the UI never stalls.
    //   CPU -> accurate value from the elevated driver helper if fresh (Phase 2), otherwise the
    //          ACPI thermal zone via WMI (MSAcpi_ThermalZoneTemperature).
    //   GPU -> user-mode vendor libraries: NVIDIA NVML, AMD ADL, Intel IGCL (max across them).
    // Every reader is lazy-inited, wrapped in try/catch and tolerant of a missing DLL, so a
    // machine that doesn't expose a sensor simply reports "no value".
    internal sealed class TempProvider : IDisposable
    {
        // ================= NVIDIA NVML (nvml.dll ships with the NVIDIA driver) =================
        [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
        private static extern int NvmlInit();
        [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")]
        private static extern int NvmlShutdown();
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2")]
        private static extern int NvmlGetCount(out uint count);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
        private static extern int NvmlGetHandle(uint index, out IntPtr device);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")]
        private static extern int NvmlGetTemp(IntPtr device, uint sensorType, out uint tempC);
        private const uint NVML_TEMPERATURE_GPU = 0;

        // ================= state =================
        private readonly object _lock = new object();
        private bool _cpuValid; private double _cpuC;   // ACPI/WMI reading
        private bool _gpuValid; private double _gpuC;   // max across vendor GPU libraries

        // Accurate CPU temp pushed in by the elevated driver helper (Phase 2); used if fresh.
        private double _extCpuC;
        private DateTime _extCpuUtc = DateTime.MinValue;

        private readonly Thread _thread;
        private volatile bool _stop;

        private bool _nvmlTried, _nvmlReady;
        private readonly AmdGpuTemp _amd = new AmdGpuTemp();
        private readonly IntelGpuTemp _intel = new IntelGpuTemp();

        public TempProvider()
        {
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "LoadView.Temps";
            _thread.Start();
        }

        // CPU: prefer the fresh helper value, else the cached ACPI/WMI reading.
        public bool TryGetCpu(out double celsius)
        {
            lock (_lock)
            {
                if (IsExtCpuFresh()) { celsius = _extCpuC; return true; }
                celsius = _cpuC; return _cpuValid;
            }
        }

        // Only the fresh helper (driver) value, if any — lets it win over the ACPI counter.
        public bool TryGetCpuHelper(out double celsius)
        {
            lock (_lock)
            {
                if (IsExtCpuFresh()) { celsius = _extCpuC; return true; }
                celsius = 0; return false;
            }
        }

        public bool TryGetGpu(out double celsius) { lock (_lock) { celsius = _gpuC; return _gpuValid; } }

        // Called by the overlay with the latest CPU package temperature from the driver helper.
        public void SetExternalCpu(double celsius)
        {
            lock (_lock) { _extCpuC = celsius; _extCpuUtc = DateTime.UtcNow; }
        }

        private bool IsExtCpuFresh()
        {
            return _extCpuUtc != DateTime.MinValue
                && (DateTime.UtcNow - _extCpuUtc).TotalSeconds < 10.0
                && _extCpuC > -50 && _extCpuC < 150;
        }

        private void Loop()
        {
            while (!_stop)
            {
                double cpu; bool cpuOk = TryReadAcpiCpu(out cpu);
                double gpu; bool gpuOk = TryReadGpu(out gpu);
                lock (_lock)
                {
                    _cpuValid = cpuOk; _cpuC = cpu;
                    _gpuValid = gpuOk; _gpuC = gpu;
                }
                for (int i = 0; i < 30 && !_stop; i++) Thread.Sleep(100); // ~3 s
            }
        }

        // ---------- CPU (ACPI thermal zone via WMI) ----------
        private static bool TryReadAcpiCpu(out double celsius)
        {
            celsius = 0;
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                    @"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
                {
                    double max = double.MinValue;
                    foreach (ManagementBaseObject mo in s.Get())
                    {
                        object v = mo["CurrentTemperature"];
                        if (v == null) continue;
                        double tenthsK = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                        double c = tenthsK / 10.0 - 273.15;
                        if (c > max) max = c;
                    }
                    if (max > -50 && max < 150) { celsius = max; return true; }
                }
            }
            catch { /* "Not supported" on many firmwares */ }
            return false;
        }

        // ---------- GPU (max across vendor libraries) ----------
        private bool TryReadGpu(out double celsius)
        {
            celsius = 0;
            double best = double.MinValue;
            double t;
            if (TryReadNvmlGpu(out t) && t > best) best = t;
            if (TryReadAdlGpu(out t) && t > best) best = t;
            if (TryReadIgclGpu(out t) && t > best) best = t;
            if (best > -50 && best < 150) { celsius = best; return true; }
            return false;
        }

        private bool TryReadNvmlGpu(out double celsius)
        {
            celsius = 0;
            if (!EnsureNvml()) return false;
            try
            {
                uint count;
                if (NvmlGetCount(out count) != 0 || count == 0) return false;
                double best = double.MinValue;
                for (uint i = 0; i < count; i++)
                {
                    IntPtr dev;
                    if (NvmlGetHandle(i, out dev) != 0) continue;
                    uint tc;
                    if (NvmlGetTemp(dev, NVML_TEMPERATURE_GPU, out tc) != 0) continue;
                    if (tc > 0 && tc < 150 && tc > best) best = tc;
                }
                if (best > 0) { celsius = best; return true; }
            }
            catch (Exception ex) { Log.Write("NVML temp", ex); }
            return false;
        }

        private bool EnsureNvml()
        {
            if (_nvmlTried) return _nvmlReady;
            _nvmlTried = true;
            try { _nvmlReady = (NvmlInit() == 0); }
            catch (Exception ex) { _nvmlReady = false; Log.Write("NVML init (no NVIDIA driver?)", ex); }
            return _nvmlReady;
        }

        // ---------- AMD ADL (atiadlxx.dll) ----------
        private bool TryReadAdlGpu(out double celsius) { return _amd.TryRead(out celsius); }

        // ---------- Intel IGCL (ControlLib) ----------
        private bool TryReadIgclGpu(out double celsius) { return _intel.TryRead(out celsius); }

        public void Dispose()
        {
            _stop = true;
            try { if (_thread != null) _thread.Join(700); } catch { }
            if (_nvmlReady) { try { NvmlShutdown(); } catch { } }
            try { _amd.Dispose(); } catch { }
            try { _intel.Dispose(); } catch { }
        }
    }
}
