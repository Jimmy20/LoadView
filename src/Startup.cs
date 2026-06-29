using System;
using Microsoft.Win32;
using System.Windows.Forms;

namespace LoadView
{
    // "Start with Windows" via the per-user Run key (no admin required).
    internal static class Startup
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "LoadView";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    return k != null && k.GetValue(ValueName) != null;
                }
            }
            catch (Exception ex) { Log.Write("Startup.IsEnabled", ex); return false; }
        }

        public static void SetEnabled(bool enabled)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (enabled)
                        k.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
                    else if (k.GetValue(ValueName) != null)
                        k.DeleteValue(ValueName, false);
                }
            }
            catch (Exception ex) { Log.Write("Startup.SetEnabled", ex); }
        }
    }
}
