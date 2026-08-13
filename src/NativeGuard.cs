using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace LoadView
{
    // Stops LoadView from loading a native DLL that someone dropped next to the exe.
    //
    // Every native library this app uses is imported by bare name — pdh.dll for the performance
    // counters, and nvml / atiadlxx / ControlLib for GPU temperatures. None of them is a KnownDLL,
    // and this is a *portable* exe that people run from Downloads, a USB stick or a shared folder,
    // where somebody else may be able to write. A file named nvml.dll placed there would be loaded
    // as our own code.
    //
    // Two things were measured here rather than assumed, and both defeated the obvious fixes:
    //
    //   * SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32) does not help. .NET Framework
    //     resolves a DllImport by probing the application base directory with an explicit full
    //     path, and a full-path load ignores search-order flags entirely. A planted nvml.dll still
    //     loaded out of the exe's directory.
    //   * Pre-loading the real library from System32 first does not help either. A full-path load
    //     maps a *distinct* module even when one with the same base name is already loaded, so the
    //     planted file was mapped as well — its DllMain ran — and only then did the export lookup
    //     fail and the runtime fall back to the System32 copy. Code execution had already happened.
    //
    // So the rule is simply: if a file with one of these names sits next to the exe, never call
    // into that library at all. A DllImport binds lazily on first call, so refusing before the
    // first call means nothing is mapped. The readers already degrade gracefully when a library is
    // absent, and losing a temperature reading beats running someone else's DllMain.
    //
    // The System32 pre-load is kept as well: it costs nothing and covers libraries that the vendor
    // DLLs themselves pull in by bare name.
    internal static class NativeGuard
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

        private static readonly string[] Guarded =
            { "pdh.dll", "nvml.dll", "atiadlxx.dll", "ControlLib.dll" };

        private static readonly HashSet<string> _blocked =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Call once, before anything triggers a native load.
        public static void Init()
        {
            string sys, exeDir;
            try
            {
                sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
                exeDir = AppDomain.CurrentDomain.BaseDirectory;
            }
            catch { return; }

            for (int i = 0; i < Guarded.Length; i++)
            {
                string name = Guarded[i];
                try
                {
                    // Anything of this name beside the exe is disqualifying, whether or not the
                    // real library also exists in System32.
                    if (File.Exists(Path.Combine(exeDir, name)))
                    {
                        _blocked.Add(name);
                        Log.Write("native guard: refusing " + name
                            + " - a file of that name sits next to the exe");
                        continue;
                    }

                    string fromSystem = Path.Combine(sys, name);
                    if (File.Exists(fromSystem)) LoadLibraryEx(fromSystem, IntPtr.Zero, 0);
                }
                catch (Exception ex) { Log.Write("native guard " + name, ex); }
            }
        }

        // True when this library must not be used at all: see part 2 above.
        public static bool Blocked(string dllName)
        {
            return _blocked.Contains(dllName);
        }
    }
}
