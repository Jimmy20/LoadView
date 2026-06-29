using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace LoadView
{
    internal struct ProcEntry
    {
        public string Name;
        public double Value; // CPU percent, or RAM bytes
    }

    // Background sampler for the top processes by CPU and by RAM, aggregated by process
    // name. Runs off the UI thread (process enumeration is relatively expensive). CPU% is
    // the delta of TotalProcessorTime over wall-time, normalized across all cores, so the
    // figures sum toward the overall CPU usage.
    internal sealed class ProcessSampler : IDisposable
    {
        private readonly object _lock = new object();
        private ProcEntry[] _topCpu = new ProcEntry[0];
        private ProcEntry[] _topRam = new ProcEntry[0];

        private readonly Thread _thread;
        private volatile bool _stop;

        private Dictionary<int, long> _prevCpu = new Dictionary<int, long>();
        private DateTime _prevTime = DateTime.MinValue;

        public ProcessSampler()
        {
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "LoadView.Procs";
            _thread.Start();
        }

        public ProcEntry[] TopCpu() { lock (_lock) { return _topCpu; } }
        public ProcEntry[] TopRam() { lock (_lock) { return _topRam; } }

        private void Loop()
        {
            while (!_stop)
            {
                try { Sample(); } catch { }
                for (int i = 0; i < 20 && !_stop; i++) Thread.Sleep(100); // ~2 s
            }
        }

        private void Sample()
        {
            Dictionary<string, long> ramByName = new Dictionary<string, long>();
            Dictionary<int, long> curCpu = new Dictionary<int, long>();
            Dictionary<int, string> curName = new Dictionary<int, string>();

            Process[] procs = Process.GetProcesses();
            foreach (Process p in procs)
            {
                try
                {
                    int pid = p.Id;
                    string name = p.ProcessName;
                    long ws = p.WorkingSet64;
                    long acc;
                    ramByName.TryGetValue(name, out acc);
                    ramByName[name] = acc + ws;

                    long cpuTicks = -1;
                    try { cpuTicks = p.TotalProcessorTime.Ticks; } catch { }
                    if (cpuTicks >= 0) { curCpu[pid] = cpuTicks; curName[pid] = name; }
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }

            DateTime now = DateTime.UtcNow;
            double wallMs = (_prevTime == DateTime.MinValue) ? 0 : (now - _prevTime).TotalMilliseconds;
            int cores = Environment.ProcessorCount; if (cores < 1) cores = 1;

            Dictionary<string, double> cpuByName = new Dictionary<string, double>();
            if (wallMs > 0)
            {
                foreach (KeyValuePair<int, long> kv in curCpu)
                {
                    long prev;
                    if (!_prevCpu.TryGetValue(kv.Key, out prev)) continue;
                    long deltaTicks = kv.Value - prev;
                    if (deltaTicks <= 0) continue;
                    double cpuMs = deltaTicks / 10000.0;             // 100 ns ticks -> ms
                    double pct = cpuMs / (wallMs * cores) * 100.0;
                    string name = curName[kv.Key];
                    double acc;
                    cpuByName.TryGetValue(name, out acc);
                    cpuByName[name] = acc + pct;
                }
            }

            _prevCpu = curCpu;
            _prevTime = now;

            ProcEntry[] topRam = TopN(ramByName, 5);
            ProcEntry[] topCpu = TopN(cpuByName, 5);

            lock (_lock)
            {
                _topRam = topRam;
                _topCpu = topCpu;
            }
        }

        private static ProcEntry[] TopN<T>(Dictionary<string, T> map, int n) where T : IConvertible
        {
            List<ProcEntry> list = new List<ProcEntry>();
            foreach (KeyValuePair<string, T> kv in map)
            {
                ProcEntry e;
                e.Name = kv.Key;
                e.Value = Convert.ToDouble(kv.Value);
                list.Add(e);
            }
            list.Sort(delegate(ProcEntry a, ProcEntry b) { return b.Value.CompareTo(a.Value); });
            if (list.Count > n) list = list.GetRange(0, n);
            return list.ToArray();
        }

        public void Dispose()
        {
            _stop = true;
            try { if (_thread != null) _thread.Join(700); } catch { }
        }
    }
}
