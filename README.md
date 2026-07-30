# LoadView

[![Latest release](https://img.shields.io/github/v/release/Jimmy20/LoadView)](https://github.com/Jimmy20/LoadView/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/Jimmy20/LoadView/total)](https://github.com/Jimmy20/LoadView/releases)
[![License: MIT](https://img.shields.io/github/license/Jimmy20/LoadView)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2010%20%2F%2011-0078D6)

**Task Manager's performance graphs, pinned to the corner of your screen.** LoadView is a
lightweight, always-on-top overlay showing live **CPU, GPU, RAM, disk and network** graphs —
plus temperatures, per-drive usage, top processes, a clock and your IP — as a **single portable
`.exe`**. No install, no dependencies.

_A tiny, portable alternative to Rainmeter, Sidebar Diagnostics and heavyweight monitoring suites._

### ⬇ [Download the latest release](https://github.com/Jimmy20/LoadView/releases/latest) &nbsp;·&nbsp; Windows 10/11 &nbsp;·&nbsp; free & open-source

<!-- Add a demo GIF/screenshot here once recorded (e.g. drop docs/demo.gif and uncomment):
![LoadView overlay](docs/demo.gif)
-->

## Highlights

- **Single portable exe** — one `LoadView.exe`, no install and no dependencies; runs on any
  Windows 10/11 PC.
- **Everything at a glance** — CPU / GPU / RAM / disk / network graphs, per-drive usage bars,
  top-5 CPU & RAM processes, LAN / WAN IP, clock, date and weekday.
- **Temperatures for every GPU** (NVIDIA / AMD / Intel, no driver needed) plus an *optional*
  accurate CPU temperature that **works even with Windows Memory Integrity turned on**.
- **Vendor-neutral & DPI-agnostic** — the same counters Task Manager uses; crisp at any scaling;
  remembers its position per screen resolution.
- **Fully configurable** — resize, reorder or hide any section; per-graph colours, max and red
  alerts; MB/s or Mbps; °C/°F; opacity; always-on-top or coverable; lock.

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
occasionally flag a freshly-downloaded build as a **false positive** — typically
**`Trojan:Win32/Wacatac.B!ml`**, a *generic* ML "catch-all" for unknown, low-reputation
executables. The `!ml` suffix means it's a heuristic guess, **not** a signature match, and the
Microsoft encyclopedia page for it is generic family boilerplate, not an analysis of this file.
Because every new unsigned release starts with near-zero reputation, this can recur on new
versions until reputation builds up (or the exe is signed).

> An older heuristic, **`Behavior:Win32/Persistence.A!ml`**, used to trip on apps writing to
> `HKCU\…\Run`. LoadView hasn't done that since v2.4.0 — it uses a Startup-folder shortcut — so
> that one shouldn't occur on current versions.

Why it happens: the exe is **unsigned** and each release is **freshly built** (near-zero download
reputation), and the *optional* accurate-CPU-temp feature downloads and installs a driver and runs
an elevated helper — benign and **off by default**, but a pattern ML models are trained on. None of
this is malware; the binary is built by GitHub Actions from the public source in this repo.

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

Enable **Settings → Temperatures → "Accurate CPU temp (driver)"** to turn it on. On first enable
LoadView offers to set it up; you get **one UAC prompt**, and then:

- It installs **[PawnIO](https://pawnio.eu/)** — a free, open-source, **digitally-signed** sensor
  driver that is **HVCI-compatible**, so accurate CPU temperature works **even with Windows Memory
  Integrity turned on** (unlike the old WinRing0 driver, which HVCI blocks). PawnIO is downloaded
  from its official source and its signature is verified before running; nothing is bundled in
  `LoadView.exe`.
- It registers a small **Task Scheduler** task so the elevated reader can start **silently from
  then on — no more UAC prompts** on later launches. The overlay itself keeps running unelevated;
  only the tiny reader is elevated (via the task).
- The reader downloads LibreHardwareMonitor (verified by SHA-256, into `%APPDATA%\LoadView\lib`),
  reads the CPU package temperature through PawnIO, and hands it to the overlay.
- If anything fails (offline, or you decline the prompts), LoadView just falls back to the ACPI /
  blank reading — nothing breaks. Diagnostics: `%APPDATA%\LoadView\helper.log`.

Leave it **off** (the default) to stay completely driver-free and admin-free. This option only
affects **CPU** temperature; GPU temperature never needs a driver.

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
