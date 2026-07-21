namespace LoadView
{
    // Single source of truth for the version string and the changelog shown in About.
    internal static class AppInfo
    {
        public const string Name = "LoadView";
        public const string Version = "2.5.1";
        public const string RepoUrl = "https://github.com/Jimmy20/LoadView";

        public static readonly string[] Changelog = new string[]
        {
            "2.5.1",
            "  - Fix false 100% CPU after waking from sleep/hibernation",
            "",
            "2.5.0",
            "  - Redesigned Settings: category sidebar, aligned rows, tooltips, live preview",
            "  - Right-click menu adds Always on top (under Lock)",
            "  - New out-of-the-box defaults (bigger clock/date/weekday, etc.)",
            "",
            "2.4.0",
            "  - Start with Windows now uses a Startup shortcut (avoids the Defender flag)",
            "  - Embedded app icon (same as tray); tray click brings the overlay to front",
            "  - Remembers window position per screen resolution",
            "  - Configurable LAN / WAN IP refresh intervals; version metadata in the exe",
            "",
            "2.3.0",
            "  - Separate download/upload colors (default green/red) for net graph + totals",
            "  - Net totals text size; centered LAN/WAN; section titles a uniform size",
            "  - Tidier drive label/bar spacing",
            "",
            "2.2.0",
            "  - Adjustable text size for Top CPU / Top RAM and IP sections",
            "  - Save current settings as defaults; Reset to defaults",
            "",
            "2.1.0",
            "  - Single instance (won't launch twice)",
            "  - Start with Windows toggle; configurable refresh interval",
            "  - GPU temp via NVIDIA NVML (no nvidia-smi process spawning)",
            "  - Lower memory churn; optional debug log",
            "  - Larger, easier-to-use Settings window; GitHub Actions build/release",
            "",
            "2.0.0",
            "  - Reorderable sections (drag order + show/hide in Settings)",
            "  - Per-graph max scale, color, and red alert threshold",
            "  - Network unit toggle (MB/s or Mbps) + session download/upload totals",
            "  - Internal + external IP; Top-5 CPU and Top-5 RAM processes",
            "  - Drives show free space, mapped network drives, sizable labels",
            "  - Date / weekday bold options; Settings centered + scrollable",
            "",
            "1.2.0",
            "  - Settings dialog: window width, graph height, drive bar height",
            "  - Show / hide each section",
            "  - Clock / date / weekday: size, color and a seconds toggle",
            "  - Drive usage bars (This PC style)",
            "  - Lock position; overlay vs background (normal window) mode",
            "  - About dialog",
            "",
            "1.1.0",
            "  - Clock, per-drive capacity, date and weekday",
            "  - Best-effort temperatures (ACPI CPU, nvidia-smi GPU)",
            "  - Network graph auto-scaling",
            "",
            "1.0.0",
            "  - Initial overlay: CPU, GPU, RAM, disk and network graphs",
        };
    }
}
