using System.Globalization;
using System.IO;
using System.Windows.Threading;
using Pulse.App.Capture;
using Pulse.App.Native;
using Pulse.App.Overlay;
using Pulse.App.Sensors;
using Pulse.App.Sessions;
using Pulse.App.Storage;
using Pulse.App.Telemetry;
using Pulse.App.UI;
using Pulse.Core.Analysis;
using Pulse.Core.Frames;
using Pulse.Core.Sessions;
using Pulse.Core.Settings;
using M = Pulse.Core.Settings.PulseSettings.Metrics;

namespace Pulse.App;

/// <summary>
/// Composition root and the 4 Hz control loop. Everything here runs on the UI thread except the producers
/// (PresentMon reader, sensor thread, session writer), which only hand over immutable data.
/// </summary>
internal sealed class PulseHost : IDisposable
{
    private const int TickMs = 250;

    private readonly SettingsStore _settingsStore = new();
    private readonly FramePipeline _pipeline = new();
    private readonly ProcessTable _processes = new();
    private readonly WheaWatcher _whea = new();
    private readonly DispatcherTimer _timer;
    private readonly Queue<(double Ms, long Increment)> _replayIncrements = new();
    private PresentMonHost? _capture;
    private SensorService? _sensors;
    private readonly GameDetector _detector;
    private AlertEngine _alerts;
    private HotkeyService? _hotkeys;
    private TrayService? _tray;
    private SessionRecorder? _recorder;
    private MainWindow? _mainWindow;
    private long _tick;
    private double _gpuLoadedSince = double.NaN;
    private long _lastReplayCounter = -1;
    private long _sessionReplays;
    private DateTime _sessionStartUtc;
    private bool _exclusiveWarned;

    public PulseHost()
    {
        Settings = _settingsStore.Load();
        Hub = new TelemetryHub
        {
            IsElevated = Startup.Elevation.IsElevated,
            PawnIoInstalled = LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled,
        };
        Store = new SessionStore(Path.Combine(Log.DataDirectory, "pulse.db"));
        _detector = new GameDetector(Settings.ToDetectorOptions());
        _alerts = new AlertEngine(Settings.BuildAlertRules());
        Overlay = new OverlayController(Settings, Hub, _pipeline, SaveSettings) { PcieReplaysRecent = () => RecentReplays() };
        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(TickMs) };
        _timer.Tick += (_, _) => Tick();
    }

    public PulseSettings Settings { get; }
    public TelemetryHub Hub { get; }
    public SessionStore Store { get; }
    public OverlayController Overlay { get; }
    public string? HotkeyProblem { get; private set; }

    public event Action? SessionsChanged;

    public void Start()
    {
        Log.Info($"Pulse starting. Elevated: {Hub.IsElevated}, PawnIO: {Hub.PawnIoInstalled}");

        _sensors = new SensorService(Hub, () => Settings.Sensors.PollIntervalMs);
        _sensors.Start();
        _whea.Start();

        _recorder = new SessionRecorder(Store, _pipeline);
        _recorder.SessionsChanged += () => SessionsChanged?.Invoke();

        if (Hub.IsElevated)
        {
            string exe = Path.Combine(AppContext.BaseDirectory, "tools", "PresentMon", "PresentMon-2.6.0-x64.exe");
            _capture = new PresentMonHost(exe, _pipeline);
            _capture.Start();
            Hub.CaptureStatus = () => _capture.Status;
        }
        else
        {
            Hub.CaptureStatus = () => "Off — needs admin";
        }

        _tray = new TrayService(ShowMainWindow, Overlay.SetMode, Overlay.ToggleHidden, () => System.Windows.Application.Current.Shutdown());
        Overlay.StateChanged += () => _tray.SetOverlayState(Overlay.Mode, Overlay.Hidden);
        _tray.SetOverlayState(Overlay.Mode, Overlay.Hidden);

        _hotkeys = new HotkeyService();
        RegisterHotkeys();

        _processes.Refresh();
        _timer.Start();
    }

    public void ShowMainWindow()
    {
        _mainWindow ??= new MainWindow(this);
        _mainWindow.ShowAndActivate();
    }

    public void SaveSettings() => _settingsStore.Save(Settings);

    /// <summary>Re-applies everything after the settings page saves.</summary>
    public void ApplySettings()
    {
        SaveSettings();
        _detector.Reconfigure(Settings.ToDetectorOptions());
        _alerts = new AlertEngine(Settings.BuildAlertRules());
        Overlay.ApplySettings();
        RegisterHotkeys();
    }

    private void RegisterHotkeys()
    {
        if (_hotkeys is null)
            return;
        _hotkeys.UnregisterAll();
        string? a = _hotkeys.Register(Settings.Hotkeys.CycleMode, Overlay.CycleMode);
        string? b = _hotkeys.Register(Settings.Hotkeys.ToggleVisible, Overlay.ToggleHidden);
        HotkeyProblem = a ?? b;
        if (HotkeyProblem is not null)
        {
            Log.Warn(HotkeyProblem);
            _tray?.Notify("Pulse hotkey", HotkeyProblem + " Change it in Settings.");
        }
    }

    // ------------------------------------------------------------------ control loop

    private void Tick()
    {
        try
        {
            double now = AppClock.NowMs;
            _tick++;

            Hub.Frames = _pipeline.ComputeStats(Settings.Overlay.StatsWindowSeconds * 1000.0);

            if (_tick % 2 == 0)
                DetectGame(now);

            if (_tick % 4 == 0)
                EverySecond(now);

            if (_tick % 40 == 0)
                _recorder?.Flush();

            Overlay.Tick();
        }
        catch (Exception ex)
        {
            Log.Error("Control tick failed", ex);
        }
    }

    private void DetectGame(double now)
    {
        if (_tick % 4 == 0)
            _processes.Refresh();

        nint hwnd = Win32.GetForegroundWindow();
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        string exe = pid == 0 ? "" : _processes.NameOf((int)pid) ?? "";
        var foreground = new ForegroundInfo((int)pid, exe, Win32.CoversMonitor(hwnd), hwnd);
        Hub.ForegroundHwnd = hwnd;
        Hub.ForegroundPid = (int)pid;

        var update = _detector.Update(now, foreground, _pipeline.GetPresentingProcesses(now), _processes.IsAlive);
        if (update.Ended is { } ended)
        {
            Log.Info($"Game ended: {ended.ExeName}");
            EndSession();
        }
        if (update.Started is { } started)
        {
            Log.Info($"Game detected: {started.ExeName} (pid {started.ProcessId})");
            _pipeline.SetActiveProcess(started.ProcessId);
            _recorder?.Start(started, now);
            _sessionStartUtc = DateTime.UtcNow;
            _sessionReplays = 0;
            _exclusiveWarned = false;
        }
        if (_detector.Current is null && _pipeline.ActiveProcessId != 0)
            _pipeline.SetActiveProcess(0);
        Hub.Game = _detector.Current;
    }

    private void EndSession()
    {
        _recorder?.SetCounters(SessionReplays(), _whea.CountSince(_sessionStartUtc));
        _recorder?.End();
    }

    private void EverySecond(double now)
    {
        HardwareReading? hw = Hub.Hardware;
        PcieReading? pcie = Hub.Pcie;
        FrameWindowStats? frames = Hub.Game is null ? null : Hub.Frames;

        // GPU counts as "under load" after 5 s above 50 % — PCIe links drop to Gen1 at idle by design.
        if (hw is { GpuLoad: > 50 })
        {
            if (double.IsNaN(_gpuLoadedSince))
                _gpuLoadedSince = now;
        }
        else
        {
            _gpuLoadedSince = double.NaN;
        }
        bool gpuLoaded = !double.IsNaN(_gpuLoadedSince) && now - _gpuLoadedSince >= 5000;

        // NVML's retry counter is cumulative since boot; track increments so a driver reset (counter drops) can't go negative.
        if (pcie is { ReplayCounter: >= 0 })
        {
            long increment = _lastReplayCounter >= 0 && pcie.ReplayCounter >= _lastReplayCounter ? pcie.ReplayCounter - _lastReplayCounter : 0;
            _lastReplayCounter = pcie.ReplayCounter;
            if (increment > 0)
            {
                _replayIncrements.Enqueue((now, increment));
                _sessionReplays += increment;
            }
        }
        while (_replayIncrements.Count > 0 && now - _replayIncrements.Peek().Ms > 60_000)
            _replayIncrements.Dequeue();

        var metrics = new Dictionary<string, double>
        {
            [M.CpuTemp] = hw?.CpuTempC ?? double.NaN,
            [M.GpuTemp] = hw?.GpuTempC ?? double.NaN,
            [M.VramPercent] = hw?.VramPercent ?? double.NaN,
            [M.RamPercent] = hw?.RamPercent ?? double.NaN,
            [M.GpuUnderLoad] = gpuLoaded ? 1 : 0,
            [M.PcieGen] = pcie?.Gen ?? double.NaN,
            [M.PcieWidth] = pcie?.Width ?? double.NaN,
            [M.PcieReplaysRecent] = RecentReplays(),
            [M.WheaRecent] = _whea.CountSince(DateTime.UtcNow.AddMinutes(-1)),
        };
        Hub.Alerts = _alerts.Evaluate(now / 1000, metrics);

        Hub.History.Push(hw, frames);

        if (_recorder is { IsRecording: true })
        {
            _recorder.AddSample(new SensorSample(
                _recorder.ElapsedSeconds(now), frames?.CurrentFps ?? double.NaN,
                hw?.CpuLoad ?? double.NaN, hw?.CpuTempC ?? double.NaN, hw?.CpuPowerW ?? double.NaN,
                hw?.GpuLoad ?? double.NaN, hw?.GpuTempC ?? double.NaN, hw?.GpuPowerW ?? double.NaN, hw?.GpuClockMhz ?? double.NaN,
                hw?.VramUsedMb ?? double.NaN, hw?.RamUsedGb ?? double.NaN,
                pcie?.Gen ?? double.NaN, pcie?.Width ?? double.NaN, gpuLoaded));
            _recorder.SetCounters(SessionReplays(), _whea.CountSince(_sessionStartUtc));
        }

        // Legacy exclusive fullscreen bypasses the desktop compositor, so no window (ours included) can draw on top.
        if (!_exclusiveWarned && Hub.Game is { } game && frames?.PresentMode == "Hardware: Legacy Flip")
        {
            _exclusiveWarned = true;
            _tray?.Notify("Overlay can't show in exclusive fullscreen",
                $"{Path.GetFileNameWithoutExtension(game.ExeName)} is in exclusive fullscreen. Switch the game to borderless/windowed fullscreen to see the overlay. Stats are still recorded.");
        }

        _tray?.SetTooltip(Tooltip(hw, frames));
    }

    private long RecentReplays() => _replayIncrements.Sum(x => x.Increment);

    private long SessionReplays() => _sessionReplays;

    private static string Tooltip(HardwareReading? hw, FrameWindowStats? frames)
    {
        static string T(double v) => double.IsFinite(v) ? v.ToString("0", CultureInfo.InvariantCulture) : "--";
        string text = $"Pulse\nGPU {T(hw?.GpuLoad ?? double.NaN)}% · {T(hw?.GpuTempC ?? double.NaN)}°C\nCPU {T(hw?.CpuLoad ?? double.NaN)}% · {T(hw?.CpuTempC ?? double.NaN)}°C";
        if (frames is not null)
            text += $"\n{T(frames.CurrentFps)} FPS · 1% {T(frames.Window.Low1Fps)}";
        return text;
    }

    public void Dispose()
    {
        _timer.Stop();
        EndSession();
        _recorder?.Dispose();
        _hotkeys?.Dispose();
        Overlay.Dispose();
        _capture?.Dispose();
        _sensors?.Dispose();
        _whea.Dispose();
        _tray?.Dispose();
        Log.Info("Pulse stopped.");
    }
}
