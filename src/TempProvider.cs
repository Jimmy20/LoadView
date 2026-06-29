using System;
using System.Diagnostics;
using System.Globalization;
using System.Management;   // requires /r:System.Management.dll
using System.Threading;

namespace LoadView
{
    // Best-effort, dependency-free temperatures:
    //   CPU  -> ACPI thermal zone via WMI (MSAcpi_ThermalZoneTemperature)
    //   GPU  -> nvidia-smi, when an NVIDIA driver is installed
    // Both run on a background thread (they can be slow / throw) and are cached, so the
    // UI never stalls. Machines that don't expose temps simply report "no value".
    internal sealed class TempProvider : IDisposable
    {
        private readonly object _lock = new object();
        private bool _cpuValid; private double _cpuC;
        private bool _gpuValid; private double _gpuC;

        private readonly Thread _thread;
        private volatile bool _stop;
        private string _nvidiaPath;

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
            _nvidiaPath = FindNvidiaSmi();
            while (!_stop)
            {
                double cpu; bool cpuOk = TryReadAcpiCpu(out cpu);
                double gpu; bool gpuOk = TryReadNvidiaGpu(out gpu);
                lock (_lock)
                {
                    _cpuValid = cpuOk; _cpuC = cpu;
                    _gpuValid = gpuOk; _gpuC = gpu;
                }
                // ~3s, but wake up promptly when asked to stop
                for (int i = 0; i < 30 && !_stop; i++) Thread.Sleep(100);
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

        private bool TryReadNvidiaGpu(out double celsius)
        {
            celsius = 0;
            if (string.IsNullOrEmpty(_nvidiaPath)) return false;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(_nvidiaPath,
                    "--query-gpu=temperature.gpu --format=csv,noheader,nounits");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(2000)) { try { p.Kill(); } catch { } return false; }

                    string first = (outp ?? "").Trim();
                    int nl = first.IndexOf('\n');
                    if (nl >= 0) first = first.Substring(0, nl).Trim();

                    double c;
                    if (double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out c) && c > 0 && c < 150)
                    {
                        celsius = c;
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static string FindNvidiaSmi()
        {
            try
            {
                string[] candidates = new string[]
                {
                    Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\nvidia-smi.exe"),
                    Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\NVIDIA Corporation\NVSMI\nvidia-smi.exe")
                };
                foreach (string c in candidates)
                    if (System.IO.File.Exists(c)) return c;
            }
            catch { }
            return null;
        }

        public void Dispose()
        {
            _stop = true;
            try { if (_thread != null) _thread.Join(700); } catch { }
        }
    }
}
