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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetDefaultDllDirectories(uint directoryFlags);

        private const uint LoadLibrarySearchUserDirs = 0x00000400;
        private const uint LoadLibrarySearchSystem32 = 0x00000800;

        // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (HANDLE)-4
        private static readonly IntPtr PerMonitorV2 = new IntPtr(-4);

        // Held for the lifetime of the process to enforce a single running instance.
        private static Mutex _instanceMutex;

        [STAThread]
        private static void Main()
        {
            // Before anything can trigger a native load. Hardening the search order helps for
            // loads that go through the normal search (including DLLs the vendor libraries pull in
            // themselves), but on its own it does NOT stop a DLL planted next to the exe: .NET
            // resolves DllImport by probing the application directory with an explicit full path,
            // which no search-order flag applies to. NativeGuard is what actually closes that —
            // see the measurement described there.
            try { SetDefaultDllDirectories(LoadLibrarySearchSystem32 | LoadLibrarySearchUserDirs); }
            catch { }
            NativeGuard.Init();

            // Elevated CPU-temperature driver helper (opt-in). Runs headless, no message loop,
            // and does not take the overlay's single-instance mutex.
            string[] argv = Environment.GetCommandLineArgs();
            if (argv.Length > 1 && argv[1] == "--temp-helper") { DriverTempHelper.Run(argv); return; }
            // One-time elevated setup: install the PawnIO driver + register the SYSTEM helper task.
            if (argv.Length > 1 && argv[1] == "--temp-setup") { PawnIoSetup.RunSetup(argv); return; }
            // Elevated undo: remove the task and everything the setup installed.
            if (argv.Length > 1 && argv[1] == "--temp-remove") { PawnIoSetup.RunRemove(); return; }

            bool createdNew;
            _instanceMutex = new Mutex(true, @"Local\LoadView_SingleInstance", out createdNew);
            if (!createdNew) return; // another LoadView is already running

            // Belt-and-suspenders with the manifest: ignored on Windows < 1703.
            try { SetProcessDpiAwarenessContext(PerMonitorV2); }
            catch { }

            // Tidy up the per-user CPU-temp files that versions before 2.10 kept in %APPDATA%
            // (the helper now runs as SYSTEM and uses %ProgramFiles% / %ProgramData% instead).
            TempIpc.CleanLegacyUserFiles();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new OverlayForm());

            GC.KeepAlive(_instanceMutex);
        }
    }
}
