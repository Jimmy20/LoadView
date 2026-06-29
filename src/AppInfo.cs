namespace LoadView
{
    // Single source of truth for the version string and the changelog shown in About.
    internal static class AppInfo
    {
        public const string Name = "LoadView";
        public const string Version = "2.1.0";
        public const string RepoUrl = "https://github.com/Jimmy20/LoadView";

        public static readonly string[] Changelog = new string[]
        {
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
