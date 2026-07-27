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

- **Drag** anywhere on the panel to move it; the position is saved **per screen resolution**
  (each display layout remembers its own spot and is restored when you return to it).
- **Right-click** the panel (or the tray icon) for: *Lock*, *Always on top*, *Reset position*,
  *Settings…*, *About*, *Exit*.
- **Left-click the tray icon** to bring the overlay to the front (even in background mode).

All settings are stored in `%APPDATA%\LoadView\settings.ini` (delete it to reset to defaults).

## Settings

Right-click → **Settings…** opens a dialog with a **category sidebar** on the left; picking a
category shows its options on the right. Changes **preview live on the overlay** as you make
them — **OK** keeps them, **Cancel** reverts. Categories:

- **Layout** — window width, graph height (applies to all graphs), drive-bar height, refresh
  interval.
- **Sections** — a checklist that controls both **visibility** (the checkbox) and **order**
  (the ▲▼ buttons) for every section: clock, each graph, net totals, top CPU, top RAM,
  drives, IP, date/weekday.
- **Graphs** — per graph: accent **color**, **max** (0 = auto / 100% default), and a red
  **alert** threshold (e.g. CPU ≥ 90 turns the whole graph red; 0 = off). The network max is
  in the selected unit.
- **Clock & date** — show-seconds; size + colour for clock/date/weekday; **bold** toggles for
  date and weekday.
- **Drives & lists** — drive-label size + bold; Top CPU/RAM text size; IP text size.
- **Network** — unit (**MB/s** bytes or **Mbps** bits); **download / upload colours** (default
  green / red); net-totals text size; **LAN / WAN IP refresh** intervals (seconds).
- **Behavior** — opacity; *Always on top*; *Lock position*; show external IP; **Start with
  Windows**; **write debug log**.
- **Defaults** — *Save current as defaults* writes your config to `defaults.ini`; *Reset to
  defaults* restores it. When `settings.ini` is absent the app falls back to `defaults.ini`
  (then to the built-in defaults), so you can copy `defaults.ini` to other machines.

Only one instance runs at a time (launching again is a no-op).

The right-click menu order is **Lock · Always on top · Reset position · Settings… · About ·
Exit**. About shows the version and changelog.

## Start with Windows

Tick **Start with Windows** in Settings. It creates a shortcut in your Startup folder
(`shell:startup`) — no admin needed. (It deliberately does **not** write the `HKCU\…\Run`
key; see *Antivirus / Defender* below.)

## Antivirus / Defender

LoadView is a single **unsigned** exe, so Microsoft Defender's machine-learning heuristics may
flag a freshly-downloaded build as a **false positive** — most commonly:

- **`Trojan:Win32/Wacatac.B!ml`** — a *generic* ML "catch-all" bucket for unknown, low-reputation
  executables. The `!ml` suffix means it's a heuristic guess, **not** a signature match, and the
  Microsoft encyclopedia page for it is generic family boilerplate, not an analysis of this file.
- **`Behavior:Win32/Persistence.A!ml`** — an older behavioral heuristic tripped by writing to
  `HKCU\…\Run`. LoadView avoids this by using a Startup-folder shortcut (above) instead.

Why it happens: the exe is **unsigned** and **newly released** (near-zero download reputation), and
the *optional* accurate-CPU-temp feature contains "download a file, then run an elevated helper"
code — benign and **off by default**, but a pattern ML models are trained on. None of this is
malware; the binary is built by GitHub Actions from the public source in this repo.

**Verify integrity:** compare your download's hash with the release asset —
`Get-FileHash LoadView.exe -Algorithm SHA256` — or upload it to
[VirusTotal](https://www.virustotal.com/): only a couple of engines flagging it with generic/`!ml`
names is the hallmark of a false positive.

If Defender flags it:

1. **Restore / allow it**: Windows Security → *Virus & threat protection* → *Protection history* →
   allow or restore the item; optionally add a folder exclusion while testing.
2. **Report the false positive** so Microsoft clears it for everyone (free, usually 1–3 days):
   <https://www.microsoft.com/wdsi/filesubmission> — submit as *Software developer* →
   *Incorrectly detected as malware*, include the SHA-256 and a link to this repo.
3. **Reputation heals it**: as more people download the same build, the ML reputation score rises
   and the detection typically fades on its own.
4. **Sign it** (the durable fix) — see below. A signed binary with reputation is trusted; CI signs
   automatically once the signing secret is set.

## Continuous build & releases

A GitHub Actions workflow ([.github/workflows/build.yml](.github/workflows/build.yml))
builds `LoadView.exe` on every push to `main` using the in-box compiler and uploads it as an
artifact; pushing a tag like `v2.1.0` publishes a GitHub Release with the exe attached.

### Code signing (optional)

The exe is unsigned by default, so SmartScreen / some corporate antivirus may warn on first
run. Where to get a certificate:

- **Azure Trusted Signing** — Microsoft's service, ~$10/month, real Authenticode, CI-friendly.
- **SignPath.io Foundation** — free code signing for open-source projects.
- **Certum Open Source** — cheap OSS cert (USB token).
- **Commercial OV/EV** (Sectigo, DigiCert, GlobalSign, SSL.com) — ~$100–700/yr; **EV** gives
  near-instant SmartScreen reputation. (Self-signed certs do **not** help.)

To sign locally:

```powershell
signtool sign /f your-cert.pfx /p PASSWORD /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 bin\LoadView.exe
```

CI signs automatically if you add repo secrets `SIGN_PFX_BASE64` (base64 of your `.pfx`)
and `SIGN_PFX_PASSWORD`; otherwise that step is skipped.

## Sections (default order, all reorderable & hideable)

| Section     | Shows                                                                 |
|-------------|-----------------------------------------------------------------------|
| Clock       | current time (`HH:mm:ss` or `HH:mm`)                                   |
| CPU         | % utilization (`· 52°C` when a temp is available)                     |
| GPU         | % of busiest GPU (`· 61°C` on NVIDIA / AMD / Intel)                   |
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

Temperatures are **best-effort** and shown next to the CPU / GPU graphs (e.g. `47% · 62°C`).
Under **Settings → Temperatures** you can switch °C/°F, hide either temperature, and set a red
**hot** threshold.

- **GPU** is read in **user-mode from the GPU vendor's own driver library** — NVIDIA (NVML),
  AMD (ADL) and Intel (IGCL) — so it works across vendors with **no admin and no extra files**
  (nothing is bundled; the libraries ship with the graphics driver). The hottest GPU is shown.
  Very old Intel iGPUs expose no sensor and simply stay blank.
- **CPU** uses the ACPI thermal zone (WMI). Many machines — most Intel laptops included — expose
  nothing there, or report a chipset zone rather than the CPU die, so the CPU temperature is
  often blank or approximate. Reading the true per-core temperature requires a **kernel driver**,
  which LoadView keeps as an **opt-in** feature (off by default) so the default stays a portable,
  no-install, no-admin single exe.

If a temperature isn't available it is simply omitted and everything else keeps working.

#### Accurate CPU temperature (optional driver)

The true per-core CPU temperature lives in the CPU's MSR registers, which can only be read from a
**kernel driver** — there is no user-mode API for it. LoadView keeps this as an **opt-in** so the
default download stays a portable, no-install, no-admin single exe.

Enable **Settings → Temperatures → "Accurate CPU temp (driver)"** to turn it on. When enabled:

- LoadView **downloads** LibreHardwareMonitor (pinned version, verified by SHA-256) into
  `%APPDATA%\LoadView\lib` — only on first enable, so the shipped `LoadView.exe` never contains any
  driver bytes.
- A small **elevated helper** (the same exe with `--temp-helper`) loads that library, reads the CPU
  package temperature through its driver, and hands it back to the overlay. You'll get **one UAC
  prompt** when it starts; the overlay itself keeps running unelevated.
- On some Windows 11 PCs, **Memory Integrity (HVCI)** blocks the driver. If so, LoadView just falls
  back to the ACPI/blank reading — nothing breaks. Diagnostics are written to
  `%APPDATA%\LoadView\helper.log`.

Leave it **off** (the default) to stay completely driver-free and admin-free.

## Notes

- Metrics refresh once per second; drives, top processes and IPs are sampled on background
  threads so a slow disk/share or web lookup never stalls the overlay.
- After resuming from sleep/hibernation the rate metrics (CPU/disk/network) briefly hold their
  last value while the performance counters re-baseline — this avoids a false 100% CPU spike.
- Disk and network values are aggregated across all physical disks / real network
  adapters (loopback and tunnel pseudo-interfaces are excluded).
- The **external IP** is fetched periodically from a public service (`api.ipify.org` over
  HTTPS); turn it off in Settings if you prefer no outbound requests. It shows `—` when offline.
- Reading performance counters does not require administrator rights.
