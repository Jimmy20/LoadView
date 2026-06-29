# LoadView

A lightweight, always-on-top overlay that shows live Task-Manager-style graphs for
**CPU, GPU, RAM, Disk, and Network (down/up)** in the corner of your screen, plus a
clock, drive usage, top processes, internal/external IP, and the date/weekday.

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

Right-click → **Settings…** opens a scrollable dialog (always centered on screen) with:

- **Layout** — window width, graph height (applies to all graphs), drive-bar height, and the
  network unit (**MB/s** bytes, or **Mbps** bits).
- **Sections** — a checklist that controls both **visibility** (the checkbox) and **order**
  (the ▲▼ buttons) for every section: clock, each graph, net totals, top CPU, top RAM,
  drives, IP, date/weekday.
- **Graphs** — per graph: accent **color**, **max** (0 = auto / 100% default), and a red
  **alert** threshold (e.g. CPU ≥ 90 turns the whole graph red; 0 = off). The network max is
  in the selected unit.
- **Clock / date** — show-seconds; size + color for clock/date/weekday; **bold** toggles for
  date and weekday; drive-label size + bold.
- **Behavior** — opacity; **refresh interval**; show external (public) IP; **Start with
  Windows**; **write debug log**; *Always on top* (uncheck for a normal coverable window);
  *Lock position*.

Only one instance runs at a time (launching again is a no-op).

The right-click menu order is **Lock · Reset position · Settings… · About · Exit**. About
shows the version and changelog.

## Start with Windows

Tick **Start with Windows** in Settings (it adds a per-user `Run` registry entry — no admin
needed). Or manually: `Win+R` → `shell:startup` → drop a shortcut to `LoadView.exe`.

## Continuous build & releases

A GitHub Actions workflow ([.github/workflows/build.yml](.github/workflows/build.yml))
builds `LoadView.exe` on every push to `main` using the in-box compiler and uploads it as an
artifact; pushing a tag like `v2.1.0` publishes a GitHub Release with the exe attached.

### Code signing (optional)

The exe is unsigned by default, so SmartScreen / some corporate antivirus may warn on first
run. To sign locally:

```powershell
signtool sign /f your-cert.pfx /p PASSWORD /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 bin\LoadView.exe
```

CI will sign automatically if you add repo secrets `SIGN_PFX_BASE64` (base64 of your `.pfx`)
and `SIGN_PFX_PASSWORD`; otherwise that step is skipped.

## Sections (default order, all reorderable & hideable)

| Section     | Shows                                                                 |
|-------------|-----------------------------------------------------------------------|
| Clock       | current time (`HH:mm:ss` or `HH:mm`)                                   |
| CPU         | % utilization (`· 52°C` when a temp is available)                     |
| GPU         | % of busiest GPU (`· 61°C` on NVIDIA GPUs)                            |
| MEM         | % physical RAM, e.g. `20.3/31.7 GB (64%)`                             |
| DISK        | % active time across **all** disks + `R / W MB/s`                    |
| NET         | down + up graph, in MB/s or Mbps                                      |
| Net totals  | session download / upload volume, e.g. `Total ↓ 1.2 GB  ↑ 350 MB`     |
| Top CPU     | top 5 processes by CPU (aggregated by name)                           |
| Top RAM     | top 5 processes by memory                                             |
| Drives      | per drive: usage bar (red ≥90%), used/total, **free space** on the right; includes mapped network drives |
| IP          | `LAN:` internal address and `WAN:` external/public address            |
| Date        | `29.06.2026` then the weekday below                                   |

If a metric's counter isn't available on a given machine, that row shows `n/a` and the
rest keep working.

### Temperatures

Temperatures are **best-effort and dependency-free**: CPU via the ACPI thermal zone
(WMI), GPU via **NVIDIA NVML** (`nvml.dll`, installed with the NVIDIA driver). Many
machines (most Intel laptops included) expose no temperature through these safe APIs —
those simply omit the temp and everything else works. Reliable temps on all hardware would
require a kernel-level driver (e.g. LibreHardwareMonitor), which needs administrator rights
and is intentionally not used here to keep the app a portable, no-install single exe.

## Notes

- Metrics refresh once per second; drives, top processes and IPs are sampled on background
  threads so a slow disk/share or web lookup never stalls the overlay.
- Disk and network values are aggregated across all physical disks / real network
  adapters (loopback and tunnel pseudo-interfaces are excluded).
- The **external IP** is fetched periodically from a public service (`api.ipify.org` over
  HTTPS); turn it off in Settings if you prefer no outbound requests. It shows `—` when offline.
- Reading performance counters does not require administrator rights.
