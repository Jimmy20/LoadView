using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LoadView
{
    internal struct MetricsSnapshot
    {
        public bool CpuValid;  public double CpuPercent;
        public bool GpuValid;  public double GpuPercent;
        public bool RamValid;  public double RamPercent; public double RamUsedBytes; public double RamTotalBytes;
        public bool DiskValid; public double DiskPercent; public double DiskReadBps; public double DiskWriteBps;
        public bool NetValid;  public double NetDownBps;  public double NetUpBps;
        // Temperatures deliberately absent: they are sensors, not metrics, and they reach the overlay
        // through TempProvider.Sensors() for the tile section.
    }

    // Samples system utilization once per call. Everything is read through PDH
    // (locale-independent) except physical memory, which uses GlobalMemoryStatusEx.
    internal sealed class MetricsSampler : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        private readonly PdhQuery _q;
        private readonly IntPtr _cpu;
        private readonly IntPtr _diskTime;
        private readonly IntPtr _diskRead;
        private readonly IntPtr _diskWrite;
        private readonly IntPtr _netRecv;
        private readonly IntPtr _netSent;
        private readonly IntPtr _gpu;
        private readonly IntPtr _thermal;
        private int _pdhTempTick;
        private readonly TempProvider _temps;

        // Refresh interval (set by the form) — used to size the sleep/resume gap threshold.
        public int IntervalMs = 1000;
        private DateTime _lastSampleUtc = DateTime.MinValue;
        private int _settleTicks;
        private MetricsSnapshot _last;

        public MetricsSampler()
        {
            _q = new PdhQuery();

            // "% Processor Time" is the actual busy-time utilization Task Manager's graph shows.
            // (Avoid "% Processor Utility": it scales by clock speed, so on turbo-capable CPUs it
            // reads far higher than Task Manager.) Fall back to the plain Processor category.
            _cpu = _q.AddCounter(@"\Processor Information(_Total)\% Processor Time");
            if (_cpu == IntPtr.Zero)
                _cpu = _q.AddCounter(@"\Processor(_Total)\% Processor Time");

            _diskTime  = _q.AddCounter(@"\PhysicalDisk(_Total)\% Disk Time");
            _diskRead  = _q.AddCounter(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
            _diskWrite = _q.AddCounter(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");

            _netRecv = _q.AddCounter(@"\Network Interface(*)\Bytes Received/sec");
            _netSent = _q.AddCounter(@"\Network Interface(*)\Bytes Sent/sec");

            // Vendor-neutral GPU utilization (NVIDIA / AMD / Intel) — the same counter
            // Task Manager uses.
            _gpu = _q.AddCounter(@"\GPU Engine(*)\Utilization Percentage");

            // ACPI thermal zone (Kelvin); absent on many machines -> handled by TempProvider.
            _thermal = _q.AddCounter(@"\Thermal Zone Information(*)\Temperature");
            _temps = new TempProvider();

            _q.Collect();
        }

        // Two collects with a short gap so the first displayed values are real
        // (rate counters need two samples to compute a delta).
        public void Warmup()
        {
            _q.Collect();
            System.Threading.Thread.Sleep(250);
            _q.Collect();
        }

        // Push an accurate CPU temperature from the elevated driver helper (Phase 2).
        public void SetCpuTempOverride(double celsius) { if (_temps != null) _temps.SetExternalCpu(celsius); }

        // Everything the temperature tiles can show right now.
        public SensorReading[] Sensors()
        {
            return _temps != null ? _temps.Sensors() : new SensorReading[0];
        }

        public MetricsSnapshot Sample()
        {
            DateTime now = DateTime.UtcNow;
            double gap = (_lastSampleUtc == DateTime.MinValue) ? 0 : (now - _lastSampleUtc).TotalSeconds;
            _lastSampleUtc = now;
            // A gap this large only happens on sleep/hibernation/long suspension (never at the
            // <=10 s refresh) — the first post-resume rate samples are bogus, so settle a few ticks.
            if (gap > Math.Max(15.0, IntervalMs / 1000.0 * 5.0)) _settleTicks = 3;

            _q.Collect();
            MetricsSnapshot s = new MetricsSnapshot();

            if (_cpu != IntPtr.Zero) { s.CpuValid = true; s.CpuPercent = Clamp(_q.ReadDouble(_cpu), 0, 100); }

            if (_diskTime != IntPtr.Zero) { s.DiskValid = true; s.DiskPercent = Clamp(_q.ReadDouble(_diskTime), 0, 100); }
            if (_diskRead != IntPtr.Zero) s.DiskReadBps = _q.ReadDouble(_diskRead);
            if (_diskWrite != IntPtr.Zero) s.DiskWriteBps = _q.ReadDouble(_diskWrite);

            if (_netRecv != IntPtr.Zero) { s.NetValid = true; s.NetDownBps = SumNet(_q.ReadArray(_netRecv)); }
            if (_netSent != IntPtr.Zero) s.NetUpBps = SumNet(_q.ReadArray(_netSent));

            if (_gpu != IntPtr.Zero) { s.GpuValid = true; s.GpuPercent = Clamp(ComputeGpu(_q.ReadArray(_gpu)), 0, 100); }

            MEMORYSTATUSEX m = new MEMORYSTATUSEX();
            m.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            if (GlobalMemoryStatusEx(ref m) && m.ullTotalPhys > 0)
            {
                s.RamValid = true;
                s.RamTotalBytes = m.ullTotalPhys;
                s.RamUsedBytes = m.ullTotalPhys - m.ullAvailPhys;
                s.RamPercent = 100.0 * s.RamUsedBytes / m.ullTotalPhys;
            }

            // The ACPI thermal-zone perf counter is a CPU-temperature source that only exists here,
            // because the PDH query lives in this class — hand it to TempProvider, which decides
            // between it, the WMI thermal zone and the driver helper's reading. Every 5th sample is
            // plenty for a fallback that many machines do not even expose.
            if (_thermal != IntPtr.Zero && (_pdhTempTick++ % 5) == 0)
            {
                double zone;
                if (TryMaxKelvinToC(_q.ReadArray(_thermal), out zone)) _temps.SetPdhCpu(zone);
            }

            // Post-resume settle: the rate counters (esp. CPU % Processor Time) read a false
            // ~100% on the first samples after a long gap. Hold the last good rate values for a
            // few ticks while the PDH baseline re-establishes; RAM + temperatures stay live.
            if (_settleTicks > 0)
            {
                _settleTicks--;
                s.CpuValid = _last.CpuValid; s.CpuPercent = _last.CpuPercent;
                s.GpuValid = _last.GpuValid; s.GpuPercent = _last.GpuPercent;
                s.DiskValid = _last.DiskValid; s.DiskPercent = _last.DiskPercent;
                s.DiskReadBps = _last.DiskReadBps; s.DiskWriteBps = _last.DiskWriteBps;
                s.NetValid = _last.NetValid; s.NetDownBps = _last.NetDownBps; s.NetUpBps = _last.NetUpBps;
            }
            else
            {
                _last = s;
            }

            return s;
        }

        private static bool TryMaxKelvinToC(List<NamedValue> items, out double celsius)
        {
            celsius = 0;
            double maxK = double.MinValue;
            foreach (NamedValue nv in items) if (nv.Value > maxK) maxK = nv.Value;
            if (maxK <= 0) return false;
            double c = maxK - 273.15;
            if (c < -50 || c > 150) return false;
            celsius = c;
            return true;
        }

        private static readonly string[] VirtualNic = { "loopback", "isatap", "teredo", "pseudo" };

        // Sum real network adapters, skipping loopback / tunnel pseudo-interfaces.
        //
        // The lowercased copy looks like waste, and replacing it with
        // IndexOf(..., OrdinalIgnoreCase) was tried — then measured, and it came out about twice as
        // slow (.NET Framework routes ignore-case IndexOf through globalization code, while
        // ToLowerInvariant plus an ordinal search stays on the fast path). Both are far below a
        // millisecond per tick, so the faster one wins and the allocation stays.
        private static double SumNet(List<NamedValue> items)
        {
            double sum = 0;
            foreach (NamedValue nv in items)
            {
                string lower = (nv.Name == null ? "" : nv.Name).ToLowerInvariant();
                bool skip = false;
                for (int i = 0; i < VirtualNic.Length; i++)
                    if (lower.IndexOf(VirtualNic[i], StringComparison.Ordinal) >= 0) { skip = true; break; }
                if (!skip) sum += nv.Value;
            }
            return sum;
        }

        // Task-Manager-style overall GPU %: instance names look like
        // "pid_1348_luid_..._phys_0_eng_0_engtype_3d". For each physical adapter we sum
        // the instances of each engine type, take the busiest engine type, and the
        // headline figure is the busiest adapter.
        // Runs every tick over every GPU-engine instance, of which a busy machine has dozens to
        // hundreds — so it allocates nothing per tick. It used to build "phys|engtype" keys out of
        // two Substring calls per instance and two fresh Dictionaries per tick, which made this the
        // app's largest source of garbage by a wide margin.
        //
        // Instead the adapter index is parsed straight to an int, the engine type is compared in
        // place inside the instance name (EngKey), and both dictionaries are reused.
        private readonly Dictionary<EngKey, double> _engSums =
            new Dictionary<EngKey, double>(EngKeyComparer.Instance);
        private readonly Dictionary<int, double> _physMax = new Dictionary<int, double>();

        private double ComputeGpu(List<NamedValue> items)
        {
            _engSums.Clear();
            foreach (NamedValue nv in items)
            {
                string name = nv.Name == null ? "" : nv.Name;
                EngKey k;
                k.Name = name;
                k.Phys = ParseIndex(name, "phys_");
                int e = name.IndexOf("engtype_", StringComparison.Ordinal);
                k.Start = e < 0 ? 0 : e + 8;
                k.Len = e < 0 ? 0 : name.Length - k.Start;

                double cur;
                if (_engSums.TryGetValue(k, out cur)) _engSums[k] = cur + nv.Value;
                else _engSums[k] = nv.Value;
            }

            _physMax.Clear();
            foreach (KeyValuePair<EngKey, double> kv in _engSums)
            {
                double cur;
                if (_physMax.TryGetValue(kv.Key.Phys, out cur))
                    _physMax[kv.Key.Phys] = Math.Max(cur, kv.Value);
                else _physMax[kv.Key.Phys] = kv.Value;
            }

            double best = 0;
            foreach (double v in _physMax.Values) if (v > best) best = v;
            return best;
        }

        // "..._phys_0_eng_..." -> 0; -1 when the marker or the digits are missing.
        private static int ParseIndex(string s, string marker)
        {
            int i = s.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return -1;
            int p = i + marker.Length, n = 0, digits = 0;
            while (p < s.Length && s[p] >= '0' && s[p] <= '9')
            { n = n * 10 + (s[p] - '0'); p++; digits++; if (digits > 9) break; }
            return digits == 0 ? -1 : n;
        }

        // An engine-type group: the adapter index plus a slice of the instance name, so grouping
        // needs no substring.
        private struct EngKey
        {
            public string Name;
            public int Phys;
            public int Start;
            public int Len;
        }

        private sealed class EngKeyComparer : IEqualityComparer<EngKey>
        {
            public static readonly EngKeyComparer Instance = new EngKeyComparer();

            public bool Equals(EngKey a, EngKey b)
            {
                if (a.Phys != b.Phys || a.Len != b.Len) return false;
                return string.CompareOrdinal(a.Name, a.Start, b.Name, b.Start, a.Len) == 0;
            }

            public int GetHashCode(EngKey k)
            {
                int h = k.Phys * 397;
                for (int i = 0; i < k.Len; i++) h = (h * 31) + k.Name[k.Start + i];
                return h;
            }
        }

        private static double Clamp(double v, double lo, double hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        public void Dispose()
        {
            if (_temps != null) _temps.Dispose();
            _q.Dispose();
        }
    }
}
