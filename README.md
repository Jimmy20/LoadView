# LoadView

A lightweight, always-on-top overlay that shows live Task-Manager-style graphs for
**CPU, GPU, RAM, Disk, and Network (down/up)** in the corner of your screen, plus a
clock, per-drive capacity, the date and the weekday.

- Native Windows app (C# / WinForms), **no install and no dependencies** — a single
  `LoadView.exe` that runs on any Windows 10/11 PC.
- **Vendor-neutral GPU** monitoring (NVIDIA / AMD / Intel) via the same performance
  counters Task Manager uses, so it works across machines.
- **Resolution / DPI agnostic**: starts top-right of the primary display, is draggable,
  and remembers its last position.
- Semi-transparent, draggable, lives in the system tray.
- **Configurable**: a Settings dialog lets you resize the window/graphs, show or hide any
  section, change clock/date/weekday size, color and the seconds toggle, set opacity, lock
  the position, and switch between always-on-top and a normal (coverable) window.

## Build

Requires nothing beyond a stock Windows 10/11 (uses the in-box .NET Framework compiler).

```powershell
./build.ps1
```

This produces `bin\LoadView.exe`.

## Run

```powershell
./bin/LoadView.exe
```

- **Drag** anywhere on the panel to move it; the position is saved (unless locked).
- **Right-click** the panel (or the tray icon) for: *Settings…*, *Lock*, *About*,
  *Reset position*, *Opacity*, *Exit*.
- **Double-click** the tray icon to show/hide.

All settings are stored in `%APPDATA%\LoadView\settings.ini` (delete it to reset to defaults).

## Settings

Right-click → **Settings…** opens a dialog with:

- **Layout** — window width, graph height (applies to all metric graphs), drive-bar height.
- **Clock / date** — show-seconds toggle; size and color for the clock, date and weekday.
- **Sections** — show/hide each of Clock, CPU, GPU, MEM, DISK, NET, Drives, Date/weekday.
- **Behavior** — opacity; *Always on top* (uncheck for a normal window other apps can cover);
  *Lock position* (disables dragging).

**Lock** and **About** (version + changelog) are also directly on the right-click menu.

## Start with Windows (optional)

Press `Win+R`, type `shell:startup`, and drop a shortcut to `LoadView.exe` in the folder
that opens.

## Layout (top to bottom)

| Section | Graph              | Readout                                   |
|---------|--------------------|-------------------------------------------|
| Clock   | —                  | current time `HH:mm:ss`                    |
| CPU     | % utilization      | `49%` (`· 52°C` when a temp is available)  |
| GPU     | % of busiest GPU   | `6%` (`· 61°C` on NVIDIA GPUs)             |
| MEM     | % physical RAM     | `20.3/31.7 GB (64%)`                       |
| DISK    | % active time      | `11%  R 0.8 / W 0.3 MB/s`                  |
| NET     | down + up, auto-scaled (no text inside the plot) | `↓ 78 Kbps  ↑ 86 Kbps` |
| DRIVES  | usage bar per drive (This PC style; red ≥90%) | each drive: `C: 143 / 235 GB (61%)` |
| Date    | —                  | `29.06.2026` then the weekday below        |

If a metric's counter isn't available on a given machine, that row shows `n/a` and the
rest keep working.

### Temperatures

Temperatures are **best-effort and dependency-free**: CPU via the ACPI thermal zone
(WMI), GPU via `nvidia-smi` when an NVIDIA driver is present. Many machines (most Intel
laptops included) expose no temperature through these safe APIs — those simply omit the
temp and everything else works. Reliable temps on all hardware would require a
kernel-level driver (e.g. LibreHardwareMonitor), which needs administrator rights and is
intentionally not used here to keep the app a portable, no-install single exe.

## Notes

- Metrics refresh once per second; idle CPU/RAM impact is negligible.
- Disk and network values are aggregated across all physical disks / real network
  adapters (loopback and tunnel pseudo-interfaces are excluded).
- Reading performance counters does not require administrator rights.
