# Pulse

A Windows hardware monitor and in-game overlay for gaming, built to go further than the NVIDIA overlay without injecting into or reading from games.

---

## 🚀 Quick start

1. Download `PulseSetup-x.y.z.exe` from [Releases](https://github.com/Jptrk/pulse/releases) and run it. No .NET install is needed; everything is bundled.
2. Click **Yes** on the admin prompt. FPS capture and CPU sensors need it. If you ticked "Start Pulse when I sign in" during setup, you won't be asked again at boot.
3. Start a game in **borderless** or windowed fullscreen. The overlay appears after about 3 seconds.
4. Press `Alt+Shift+O` to switch overlay layouts.
5. Click the green Pulse icon in the system tray to open the dashboard.

> 💡 Closing the window keeps Pulse running in the tray. To quit, right-click the tray icon → **Exit**.

## ⌨️ Hotkeys

| Keys | What it does |
|---|---|
| `Alt+Shift+O` | Cycle overlay: Strip → Panel → Detailed → Off |
| `Alt+Shift+H` | Hide or show the overlay |

Change them in **Settings → Hotkeys**.

---

## 📺 What it shows

### Overlay layouts

| | Strip | Panel | Detailed |
|---|:-:|:-:|:-:|
| FPS, 1% low | ✓ | ✓ | ✓ |
| 0.1% low, live frame-time graph with stutter spikes in red | | ✓ | ✓ |
| GPU load / temp / power / clock / VRAM | ✓ (core) | ✓ | ✓ |
| CPU load / temp / power / clock, busiest thread | ✓ (core) | ✓ | ✓ |
| RAM | ✓ | ✓ | ✓ |
| Battery %, drain/charge watts, time left (handhelds, laptops) | ✓ | ✓ | ✓ |
| **GPU-bound / CPU-bound / Capped** indicator, GPU busy % | ✓ | ✓ | ✓ |
| Display + PC latency, p99 frame time, stutter count | | | ✓ |
| Fan rpm, VRAM junction temp, per-thread load bars | | | ✓ |
| PCIe link (gen × width) and link retries per minute | | | ✓ |
| Alerts (hot CPU/GPU, VRAM/RAM full, PCIe link wrong, WHEA errors) | ✓ | ✓ | ✓ |

### Dashboard

Open it from the tray icon. It has:
- live cards and 5-minute charts
- session history with frame-time and temperature charts for each session
- a side-by-side comparison of any two sessions
- CSV export
- settings

### Session history

Pulse records each game session automatically. It keeps every frame time plus sensor readings once per second, so these are all exact:
- averages and 1% / 0.1% lows
- stutters and max temps
- % of time GPU-bound
- PCIe link under load and WHEA errors

### What NVIDIA's overlay doesn't have

- 0.1% lows and stutter detection with highlighted spikes
- A CPU-bound vs GPU-bound verdict, from PresentMon's GPU-busy timing
- Busiest CPU thread and per-thread bars, which catch single-thread bottlenecks that total CPU % hides
- PCIe link watch: a warning under load if the GPU link isn't the expected Gen/width, or if it starts retrying transfers (a sign of a marginal riser)
- Every session saved, browsable and comparable, with CSV export

---

## 👁️ When the overlay shows

- It works over **borderless/windowed fullscreen** and most modern "fullscreen" games, which Windows runs as fullscreen-optimized.
- **Legacy exclusive fullscreen** hides every other window. Pulse detects this and sends a tray notification; switch the game to borderless.
- When you alt-tab to another app on the same monitor, the overlay hides. An app on a different monitor doesn't hide it.

> 💡 **Want the overlay with no game running?** Go to **Settings → In-game overlay**, untick **"Show overlay only while a game is detected"**, then click **Save changes**. Hardware stats stay live on your main screen; FPS shows "--" until a game starts.

## 🎒 Handhelds and laptops

Pulse works on handheld PCs (MSI Claw, ROG Ally, Legion Go and others) and laptops:
- **Battery**: level, drain or charge rate in watts, and time left. It comes from the standard Windows battery interface, so it works on any device.
- **Low-battery warning** at 15% while unplugged. Change it in **Settings → Alerts**.
- **Chip watts**: on handhelds the CPU and GPU share one chip, so the CPU watts figure is the whole chip. Under load this matches the power mode (TDP) you set in your handheld's control app.
- **GPU watts** show separately only where the chip reports them on their own, such as on some Intel chips.
- **GPU memory** shows the system RAM the integrated GPU is using, including any amount reserved for it.

Reading the *configured* TDP limit itself, as opposed to actual draw, would need the optional PawnIO driver, so it isn't shown.

## 🎮 Game detection

- A game is the focused app drawing **20+ frames/s** that fills its monitor, sustained for 3 seconds.
- Browsers, launchers, Discord, Sunshine, Wallpaper Engine and similar apps are never counted as games.
- A session survives alt-tabbing. It ends when the game exits or stops drawing for 2 minutes.
- Sessions shorter than 30 seconds are discarded.
- For **windowed** games, add the exe name under **Settings → Game detection → Always a game**.

---

## 🛡️ Anti-cheat safety

Pulse is built never to touch a game process:

- **No injection, no hooks, no game memory access.** The overlay is a separate click-through window, like a chat popup.
- **Pulse itself never opens the game process.**
  - Game name and PID come from PresentMon's output and the foreground window.
  - Process lists come from a toolhelp snapshot.
- **FPS** comes from PresentMon 2.6.0 (Intel, MIT, signed). It reads Windows' own ETW frame events. NVIDIA FrameView and CapFrameX use the same method.
  - One caveat: PresentMon opens each process that draws frames with `PROCESS_QUERY_LIMITED_INFORMATION`, to read its exe name and notice when it exits (`PresentMon/OutputThread.cpp`).
  - That's the weakest access right Windows has. It cannot read or write memory or inject. Task Manager, Steam and Discord use the same access.
  - The PawnIO gate below covers it in practice.
- **GPU sensors** come from NVAPI/NVML, the user-mode libraries `nvidia-smi` uses.
- **No capture-hiding tricks** (`WDA_EXCLUDEFROMCAPTURE`), because cheat overlays use them.

The only kernel-level piece is **optional**: the signed PawnIO driver, which is needed only for CPU temperature (and integrated-GPU temperature on handhelds). CPU watts come from Windows' built-in Energy Meter counters, so they work without it.

### PawnIO gate (do this before installing the driver)

1. Run Pulse **without** PawnIO. Play VALORANT and Battlefield 6 for a match each and confirm both launch and play normally.
2. Install the driver: `winget install namazso.PawnIO`. Restart Pulse.
3. Play both again.
4. If Vanguard or Javelin shows a "blocked driver" popup, uninstall PawnIO from *Apps & features*.

Pulse works fully without PawnIO, except CPU temperature.

> ⚠️ FACEIT has blocked PawnIO in the past; Vanguard and Javelin are unconfirmed either way. The worst case is a block popup, not a ban.

---

## 🧮 How metrics are computed

- **Avg FPS** = frames ÷ total time (time-weighted).
- **1% low** = 1000 ÷ the mean of the slowest 1% of frame times. **0.1% low** uses the slowest 0.1%.
  - The overlay uses a trailing 30 s window (configurable).
  - Sessions use every frame.
- **Stutter** = a frame longer than max(2 × rolling median, median + 8 ms).
- **Bottleneck**:
  - **GPU-bound**: GPU busy ≥ 85% of frame time.
  - **Capped**: the app waits ≥ 25% of each frame (frame limiter or V-Sync).
  - **CPU-bound**: GPU busy < 70%.
  - **Balanced**: anything else.
  - Uses 1 s medians. A new verdict must hold for 2 s before the label changes.
- **CPU watts** = whole-package power from the CPU's own energy counters, read through Windows' Energy Meter. These are the same hardware counters PawnIO would read, without the driver.
- **PCIe** is checked only after 5 s above 50% GPU load, because links slow down at idle by design.

---

## 🛠️ Build it yourself

```powershell
# Build and run (debug)
dotnet build Pulse.sln
.\src\Pulse.App\bin\Debug\net10.0-windows\Pulse.exe

# Release build into .\publish
powershell -ExecutionPolicy Bypass -File .\publish.ps1
```

- If you decline the admin prompt, Pulse still runs, with FPS and CPU temperature off.
- To skip the admin prompt at every boot, turn on **Settings → Start Pulse when I sign in**. This creates a Task Scheduler entry that runs with highest privileges, no time limit and normal priority.
- Run the tests with `dotnet test`.

## 🚢 Making a release

```powershell
# One-time setup
winget install JRSoftware.InnoSetup

# Build the installer → artifacts\PulseSetup-<version>.exe (+ .sha256)
powershell -ExecutionPolicy Bypass -File .\release.ps1

# Same, and publish it as a GitHub release (tag v<version>, needs gh CLI)
powershell -ExecutionPolicy Bypass -File .\release.ps1 -Upload
```

- **Version** comes from `<Version>` in `Directory.Build.props`. Override it with `-Version 0.2.0`. Bump it for every release.
- The script runs the tests first and stops if any fail.
- It builds a **self-contained** copy, so users don't need .NET.
- The installer:
  - installs to Program Files, with a Start menu shortcut and an optional desktop shortcut
  - has an optional "start at sign-in" checkbox
  - closes a running Pulse cleanly on upgrade
  - its uninstaller removes the autostart task and asks before deleting saved sessions and settings
- The installer isn't code-signed, so Windows SmartScreen shows "Windows protected your PC" on first run → **More info → Run anyway**.
- The installer script is in `installer/Pulse.iss`.

## 📁 Where your data lives

| What | Where |
|---|---|
| Settings | `%AppData%\Pulse\settings.json` |
| Session database (SQLite) | `%LocalAppData%\Pulse\pulse.db` |
| Log | `%LocalAppData%\Pulse\pulse.log` |

## 💻 Command line

| Flag | Purpose |
|---|---|
| `--minimized` | Start in the tray (used by autostart) |
| `--no-elevate` | Don't ask for admin |
| `--preview DIR` | Render overlay mock-ups to PNG and exit |
| `--snapshot DIR` | Render each dashboard page to PNG and exit |
| `--dump-sensors FILE` | Write every sensor LibreHardwareMonitor sees and exit |
| `--exit-after SECONDS` | Quit after N seconds (smoke tests) |
| `--quit` | Ask a running Pulse to exit cleanly and wait (used by the installer) |
| `--register-autostart` / `--unregister-autostart` | Add or remove the sign-in task, then exit (used by the installer) |

## 🧩 Project layout

```
src/Pulse.Core/          Pure logic, fully unit-tested: PresentMon CSV parser, frame stats,
                         stutter + bottleneck analysis, game detection, alerts, session summary, settings
src/Pulse.App/           WPF app
  Capture/               PresentMon child process (kill-on-close job object, auto-restart)
  Sensors/               LibreHardwareMonitor (CPU/GPU/RAM), NVML PCIe link, WHEA event watcher
  Overlay/               DrawingVisual renderer (no WPF controls), click-through topmost window
  Sessions/, Storage/    Session recorder, SQLite store (Brotli-compressed frame times), settings JSON
  UI/                    Dashboard (Live / Sessions / Settings), tray
installer/               Inno Setup script for the installer
tests/Pulse.Core.Tests/  xUnit tests, including a real PresentMon capture fixture
tools/PresentMon/        Pinned PresentMon 2.6.0 (SHA-256 in SHA256SUMS.txt) + MIT license
```
