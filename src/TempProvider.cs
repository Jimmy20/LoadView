using System;
using System.Collections.Generic;
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

        // ACPI thermal zone read via PDH, pushed in from MetricsSampler.
        private double _pdhC;
        private bool _pdhValid;
        private DateTime _pdhUtc = DateTime.MinValue;

        private readonly Thread _thread;
        private volatile bool _stop;

        private bool _nvmlTried, _nvmlReady;
        private readonly AmdGpuTemp _amd = new AmdGpuTemp();
        private readonly IntelGpuTemp _intel = new IntelGpuTemp();
        private readonly DiskTemp _disk = new DiskTemp();
        private SensorReading[] _diskReadings;
        private SensorReading[] _extra;

        public TempProvider()
        {
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "LoadView.Temps";
            _thread.Start();
        }

        // CPU, best source first: the driver helper's reading, then the WMI thermal zone, then the
        // PDH thermal-zone counter (which only MetricsSampler can read, so it is pushed in here).
        public bool TryGetCpu(out double celsius)
        {
            lock (_lock)
            {
                if (IsExtCpuFresh()) { celsius = _extCpuC; return true; }
                if (_cpuValid) { celsius = _cpuC; return true; }
                if (_pdhValid && (DateTime.UtcNow - _pdhUtc).TotalSeconds < 30)
                { celsius = _pdhC; return true; }
                celsius = 0; return false;
            }
        }

        // The ACPI thermal zone as read through the performance counters, handed over by
        // MetricsSampler because that is where the PDH query lives.
        public void SetPdhCpu(double celsius)
        {
            if (celsius <= -50 || celsius >= 150) return;
            lock (_lock) { _pdhC = celsius; _pdhValid = true; _pdhUtc = DateTime.UtcNow; }
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
            int pass = 0;
            while (!_stop)
            {
                double cpu; bool cpuOk = TryReadAcpiCpu(out cpu);
                double gpu; bool gpuOk = TryReadGpu(out gpu);

                // Disks move slowly and each reading is a handle open plus an IOCTL, so once every
                // ~12 s is plenty; the drive list itself is re-enumerated every ~5 minutes so a
                // newly attached drive still shows up without paying for it on every pass.
                SensorReading[] disks = null;
                try
                {
                    if (pass % 100 == 0) _disk.Rescan();
                    if (pass % 4 == 0) disks = _disk.Read();
                }
                catch (Exception ex) { Log.Write("disk temperature", ex); }

                lock (_lock)
                {
                    _cpuValid = cpuOk; _cpuC = cpu;
                    _gpuValid = gpuOk; _gpuC = gpu;
                    if (disks != null) _diskReadings = disks;
                }
                pass++;
                for (int i = 0; i < 30 && !_stop; i++) Thread.Sleep(100); // ~3 s
            }
        }

        // Everything readable right now, as tiles want it: the CPU (the driver helper's value when
        // it is fresh, else ACPI), each GPU-capable vendor's reading, and every disk that answers.
        private readonly List<SensorReading> _sensorBuf = new List<SensorReading>();

        public SensorReading[] Sensors()
        {
            List<SensorReading> list = _sensorBuf;
            list.Clear();
            double v;
            if (TryGetCpu(out v)) list.Add(Make("cpu", "CPU", "CPU package", v));
            if (TryGetGpu(out v)) list.Add(Make("gpu", "GPU", "Graphics processor", v));
            lock (_lock)
            {
                if (_diskReadings != null) list.AddRange(_diskReadings);
                if (_extra != null) list.AddRange(_extra);
            }
            return list.ToArray();
        }

        // Readings pushed in from the elevated helper (chipset, fans). Empty until stage 3 ships.
        public void SetExtraSensors(SensorReading[] readings)
        {
            lock (_lock) { _extra = readings; }
        }

        private static SensorReading Make(string id, string label, string detail, double value)
        {
            SensorReading r;
            r.Id = id; r.Label = label; r.Detail = detail;
            r.Kind = SensorKind.Temperature; r.Value = value;
            return r;
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
            if (NativeGuard.Blocked("nvml.dll")) { _nvmlReady = false; return false; }
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
