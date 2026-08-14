using System;
using System.Drawing;
using Microsoft.Win32;

namespace LoadView
{
    internal enum ThemeMode
    {
        System,   // follow the Windows app theme, and follow it when it changes
        Dark,
        Light
    }

    // The whole palette in one place. Panels read these at paint time rather than caching a colour
    // in a static field, so switching theme is a repaint and not a restart.
    //
    // Note the deliberate asymmetry: the light palette is not the dark one inverted. Grey-on-white
    // needs more contrast than grey-on-black to read as "dim" rather than "broken", and a solid
    // white background under a translucent always-on-top window is harsh, so the light surface is a
    // very light grey instead.
    internal static class Theme
    {
        public static bool IsDark = true;

        // window and section surfaces
        public static Color WindowBack { get { return IsDark ? C(12, 12, 14) : C(238, 238, 242); } }
        public static Color PanelBack { get { return IsDark ? C(26, 26, 30) : C(250, 250, 252); } }
        public static Color TileBack { get { return IsDark ? C(38, 38, 44) : C(232, 232, 238); } }
        public static Color Grid { get { return IsDark ? C(45, 45, 52) : C(214, 214, 220); } }
        public static Color BarTrack { get { return IsDark ? C(52, 52, 60) : C(208, 208, 216); } }
        public static Color BarEdge { get { return IsDark ? C(70, 70, 78) : C(186, 186, 196); } }

        // text
        public static Color Text { get { return IsDark ? C(232, 232, 237) : C(28, 28, 32); } }
        public static Color Value { get { return IsDark ? C(228, 228, 233) : C(28, 28, 32); } }
        public static Color Dim { get { return IsDark ? C(150, 150, 158) : C(104, 104, 112); } }

        // dialogs
        public static Color DialogBack { get { return IsDark ? C(32, 32, 36) : C(243, 243, 246); } }
        public static Color NavBack { get { return IsDark ? C(24, 24, 28) : C(228, 228, 234); } }
        public static Color FieldBack { get { return IsDark ? C(46, 46, 52) : C(255, 255, 255); } }
        public static Color ButtonBack { get { return IsDark ? C(56, 56, 64) : C(226, 226, 232); } }
        public static Color Border { get { return IsDark ? C(90, 90, 98) : C(176, 176, 186); } }
        public static Color Accent { get { return IsDark ? C(0x6F, 0xA8, 0xFF) : C(0x1F, 0x5F, 0xC7); } }

        // Alert red works on both, but needs to be darker on white to keep its weight.
        public static Color Alert { get { return IsDark ? C(0xE0, 0x4F, 0x4F) : C(0xC0, 0x28, 0x28); } }

        private static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }

        // ---- resolving the mode ----

        public static void Apply(ThemeMode mode)
        {
            IsDark = (mode == ThemeMode.Dark) || (mode == ThemeMode.System && SystemPrefersDark());
        }

        // Windows records the app theme (as opposed to the taskbar's) in AppsUseLightTheme: 0 = dark.
        // Missing value means light, which is what Windows itself assumes.
        public static bool SystemPrefersDark()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k == null) return false;
                    object v = k.GetValue("AppsUseLightTheme");
                    if (v == null) return false;
                    return Convert.ToInt32(v) == 0;
                }
            }
            catch { return false; }
        }

        // ---- user-chosen colours ----
        //
        // Clock, date and weekday colours are settings, and their defaults assume a dark background:
        // pure white and pale yellow would be invisible on the light surface. When the effective
        // theme flips, a colour still sitting at the *other* theme's default is treated as "never
        // chosen" and swapped for this theme's default; anything else is the user's decision and is
        // left alone.
        public static Color DefaultClock(bool dark) { return dark ? C(255, 255, 255) : C(24, 24, 28); }
        public static Color DefaultDate(bool dark) { return dark ? C(232, 232, 237) : C(48, 48, 54); }
        public static Color DefaultDay(bool dark) { return dark ? C(255, 255, 128) : C(140, 106, 0); }

        public static Color Remap(Color current, bool nowDark, Func<bool, Color> defaults)
        {
            Color other = defaults(!nowDark);
            return SameRgb(current, other) ? defaults(nowDark) : current;
        }

        private static bool SameRgb(Color a, Color b)
        {
            return a.R == b.R && a.G == b.G && a.B == b.B;
        }
    }
}
