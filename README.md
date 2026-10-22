# Pulse

A Windows hardware monitor and in-game overlay for gaming, built to go further than the NVIDIA overlay without injecting into or reading from games.

## What it shows

**Overlay**: three layouts. `Alt+Shift+O` cycles Strip → Panel → Detailed → Off, and `Alt+Shift+H` hides or shows it.

| | Strip | Panel | Detailed |
|---|:-:|:-:|:-:|
| FPS, 1% low | ✓ | ✓ | ✓ |
| 0.1% low, live frame-time graph with stutter spikes in red | | ✓ | ✓ |
| GPU load / temp / power / clock / VRAM | ✓ (core) | ✓ | ✓ |
| CPU load / temp / power / clock, busiest thread | ✓ (core) | ✓ | ✓ |
| RAM | ✓ | ✓ | ✓ |
| **GPU-bound / CPU-bound / Capped** indicator, GPU busy % | ✓ | ✓ | ✓ |
| Display + PC latency, p99 frame time, stutter count | | | ✓ |
| Fan rpm, VRAM junction temp, per-thread load bars | | | ✓ |
| PCIe link (gen × width) and link retries per minute | | | ✓ |
| Alerts (hot CPU/GPU, VRAM/RAM full, PCIe link wrong, WHEA errors) | ✓ | ✓ | ✓ |

**Dashboard** (tray icon → Open): live cards, 5-minute charts, session history with per-session frame-time and temperature charts, compare any two sessions, CSV export, settings.

**Session history**: each game session is recorded automatically. Pulse keeps every frame time plus 1 Hz sensors, so averages, 1%/0.1% lows, stutters, max temps, GPU-bound %, PCIe link under load and WHEA errors are all exact.

### What NVIDIA's overlay doesn't have

- 0.1% lows and stutter detection with highlighted spikes
- CPU-bound vs GPU-bound verdict from PresentMon's GPU-busy timing
- Busiest CPU thread and per-thread bars, which catch single-thread bottlenecks hidden by total CPU %
- PCIe link watch: warns under load if the GPU link isn't the expected Gen/width, or if it starts retrying transfers (a marginal riser)
- Every session saved, browsable and comparable, with CSV export

## How metrics are computed

- **Avg FPS** = frames ÷ total time (time-weighted).
- **1% low** = 1000 ÷ mean of the slowest 1% of frame times. **0.1% low** uses the slowest 0.1%. The overlay uses a trailing 30 s window (configurable); sessions use every frame.
- **Stutter** = a frame longer than max(2 × rolling median, median + 8 ms).
- **Bottleneck**:
  - **GPU-bound**: GPU busy ≥ 85% of frame time.
  - **Capped**: the app waits ≥ 25% of each frame (limiter or V-Sync).
  - **CPU-bound**: GPU busy < 70%.
  - **Balanced**: anything else.
  - Uses 1 s medians, and a new verdict must hold for 2 s before the label changes.
- **PCIe** is checked only after 5 s above 50% GPU load. Links idle down by design.

## Anti-cheat safety

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

The only kernel-level piece is **optional**: the signed PawnIO driver, which is needed only for CPU temperature and CPU power.

### PawnIO gate (do this before installing the driver)

1. Run Pulse **without** PawnIO. Play VALORANT and Battlefield 6 for a match each and confirm both launch and play normally.
2. Install the driver: `winget install namazso.PawnIO`. Restart Pulse.
3. Play both again.
4. If Vanguard or Javelin shows a "blocked driver" popup, uninstall PawnIO from *Apps & features*.

Pulse works fully without PawnIO, except CPU temperature and CPU power. FACEIT has blocked PawnIO in the past; Vanguard and Javelin are unconfirmed either way. The worst case is a block popup, not a ban.

## Run it

```powershell
# Build and run (debug)
dotnet build Pulse.sln
.\src\Pulse.App\bin\Debug\net10.0-windows\Pulse.exe

# Release build into .\publish
powershell -ExecutionPolicy Bypass -File .\publish.ps1
```

Pulse asks for admin on start (UAC), because FPS capture and CPU sensors need it. If you decline, it runs with FPS and CPU temperature off.

To skip the UAC prompt at every boot, enable **Settings → Start Pulse when I sign in**. This creates a Task Scheduler entry with highest privileges, no time limit and normal priority.

Closing the window keeps Pulse in the tray. Exit from the tray menu.

### Overlay visibility

The overlay shows over **borderless/windowed fullscreen**, and over most modern "fullscreen" games, which Windows runs as fullscreen-optimized. **Legacy exclusive fullscreen** hides every other window. Pulse detects this and sends a tray notification; switch the game to borderless.

### Game detection

A game is the focused app drawing at least 20 frames/s that fills its monitor, sustained for 3 s. Browsers, launchers, Discord, Sunshine, Wallpaper Engine and similar apps are excluded. A session survives alt-tabbing and ends when the game exits or stops drawing for 2 minutes. Sessions shorter than 30 s are discarded.

For windowed games, add the exe under **Settings → Game detection → Always a game**.

## Files

| What | Where |
|---|---|
| Settings | `%AppData%\Pulse\settings.json` |
| Session database (SQLite) | `%LocalAppData%\Pulse\pulse.db` |
| Log | `%LocalAppData%\Pulse\pulse.log` |

## Command line

| Flag | Purpose |
|---|---|
| `--minimized` | Start in the tray (used by autostart) |
| `--no-elevate` | Don't ask for admin |
| `--preview DIR` | Render overlay mock-ups to PNG and exit |
| `--snapshot DIR` | Render each dashboard page to PNG and exit |
| `--dump-sensors FILE` | Write every sensor LibreHardwareMonitor sees and exit |
| `--exit-after SECONDS` | Quit after N seconds (smoke tests) |

## Project layout

```
src/Pulse.Core/          Pure logic, fully unit-tested: PresentMon CSV parser, frame stats,
                         stutter + bottleneck analysis, game detection, alerts, session summary, settings
src/Pulse.App/           WPF app
  Capture/               PresentMon child process (kill-on-close job object, auto-restart)
  Sensors/               LibreHardwareMonitor (CPU/GPU/RAM), NVML PCIe link, WHEA event watcher
  Overlay/               DrawingVisual renderer (no WPF controls), click-through topmost window
  Sessions/, Storage/    Session recorder, SQLite store (Brotli-compressed frame times), settings JSON
  UI/                    Dashboard (Live / Sessions / Settings), tray
tests/Pulse.Core.Tests/  xUnit tests, including a real PresentMon capture fixture
tools/PresentMon/        Pinned PresentMon 2.6.0 (SHA-256 in SHA256SUMS.txt) + MIT license
```

Run the tests with `dotnet test`.
