using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Windows.Forms;

namespace LoadView
{
    // "Start with Windows" via a shortcut in the user's Startup folder.
    //
    // NOTE: earlier versions wrote HKCU\...\Run, which Windows Defender's behavioral engine
    // flags as Behavior:Win32/Persistence.A!ml for an unsigned binary. A Startup-folder
    // shortcut is the standard, less-suspicious mechanism (and needs no admin).
    internal static class Startup
    {
        private const string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "LoadView";

        private static string ShortcutPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "LoadView.lnk");
        }

        public static bool IsEnabled()
        {
            try { return File.Exists(ShortcutPath()); }
            catch (Exception ex) { Log.Write("Startup.IsEnabled", ex); return false; }
        }

        public static void SetEnabled(bool enabled)
        {
            try
            {
                string path = ShortcutPath();
                if (enabled) CreateShortcut(path);
                else if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) { Log.Write("Startup.SetEnabled", ex); }
        }

        // Remove the HKCU\Run value written by older versions (the Defender trigger).
        public static void RemoveLegacyRunKey()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(LegacyRunKey, true))
                {
                    if (k != null && k.GetValue(ValueName) != null)
                        k.DeleteValue(ValueName, false);
                }
            }
            catch (Exception ex) { Log.Write("Startup.RemoveLegacyRunKey", ex); }
        }

        // Build the .lnk via the WScript.Shell COM object, late-bound (no COM reference needed).
        private static void CreateShortcut(string lnkPath)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) { Log.Write("Startup: WScript.Shell unavailable"); return; }

            object shell = Activator.CreateInstance(shellType);
            try
            {
                object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                    new object[] { lnkPath });
                Type t = sc.GetType();
                string exe = Application.ExecutablePath;
                t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { exe });
                t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc,
                    new object[] { Path.GetDirectoryName(exe) });
                t.InvokeMember("Description", BindingFlags.SetProperty, null, sc,
                    new object[] { "LoadView system monitor overlay" });
                t.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                Marshal.ReleaseComObject(sc);
            }
            finally
            {
                Marshal.ReleaseComObject(shell);
            }
        }
    }
}
