using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace LoadView
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (HANDLE)-4
        private static readonly IntPtr PerMonitorV2 = new IntPtr(-4);

        // Held for the lifetime of the process to enforce a single running instance.
        private static Mutex _instanceMutex;

        [STAThread]
        private static void Main()
        {
            bool createdNew;
            _instanceMutex = new Mutex(true, @"Local\LoadView_SingleInstance", out createdNew);
            if (!createdNew) return; // another LoadView is already running

            // Belt-and-suspenders with the manifest: ignored on Windows < 1703.
            try { SetProcessDpiAwarenessContext(PerMonitorV2); }
            catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new OverlayForm());

            GC.KeepAlive(_instanceMutex);
        }
    }
}
