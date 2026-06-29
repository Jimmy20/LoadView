namespace LoadView
{
    // Single source of truth for the version string and the changelog shown in About.
    internal static class AppInfo
    {
        public const string Name = "LoadView";
        public const string Version = "1.2.0";
        public const string RepoUrl = "https://github.com/Jimmy20/LoadView";

        public static readonly string[] Changelog = new string[]
        {
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
