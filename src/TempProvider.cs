using System;
using System.Globalization;
using System.Management;   // requires /r:System.Management.dll
using System.Runtime.InteropServices;
using System.Threading;

namespace LoadView
{
    // Best-effort, dependency-free temperatures:
    //   CPU -> ACPI thermal zone via WMI (MSAcpi_ThermalZoneTemperature)
    //   GPU -> NVIDIA NVML (nvml.dll), installed with the NVIDIA driver
    // Runs on a background thread (queries can be slow / throw) and is cached, so the UI
    // never stalls. Machines that don't expose temps simply report "no value".
    internal sealed class TempProvider : IDisposable
    {
        // ---- NVML (nvml.dll ships with the NVIDIA driver; absent elsewhere) ----
        [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
        private static extern int NvmlInit();
        [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")]
        private static extern int NvmlShutdown();
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
        private static extern int NvmlGetHandle(uint index, out IntPtr device);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")]
        private static extern int NvmlGetTemp(IntPtr device, uint sensorType, out uint tempC);
        private const uint NVML_TEMPERATURE_GPU = 0;

        private readonly object _lock = new object();
        private bool _cpuValid; private double _cpuC;
        private bool _gpuValid; private double _gpuC;

        private readonly Thread _thread;
        private volatile bool _stop;

        private bool _nvmlTried;
        private bool _nvmlReady;

        public TempProvider()
        {
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "LoadView.Temps";
            _thread.Start();
        }

        public bool TryGetCpu(out double celsius) { lock (_lock) { celsius = _cpuC; return _cpuValid; } }
        public bool TryGetGpu(out double celsius) { lock (_lock) { celsius = _gpuC; return _gpuValid; } }

        private void Loop()
        {
            while (!_stop)
            {
                double cpu; bool cpuOk = TryReadAcpiCpu(out cpu);
                double gpu; bool gpuOk = TryReadNvmlGpu(out gpu);
                lock (_lock)
                {
                    _cpuValid = cpuOk; _cpuC = cpu;
                    _gpuValid = gpuOk; _gpuC = gpu;
                }
                for (int i = 0; i < 30 && !_stop; i++) Thread.Sleep(100); // ~3 s
            }
        }

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

        private bool TryReadNvmlGpu(out double celsius)
        {
            celsius = 0;
            if (!EnsureNvml()) return false;
            try
            {
                IntPtr dev;
                if (NvmlGetHandle(0, out dev) != 0) return false;
                uint t;
                if (NvmlGetTemp(dev, NVML_TEMPERATURE_GPU, out t) != 0) return false;
                if (t > 0 && t < 150) { celsius = t; return true; }
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

        public void Dispose()
        {
            _stop = true;
            try { if (_thread != null) _thread.Join(700); } catch { }
            if (_nvmlReady) { try { NvmlShutdown(); } catch { } }
        }
    }
}
