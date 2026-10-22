using System.Windows.Threading;
using Pulse.App.Native;
using Pulse.App.Telemetry;
using Pulse.Core.Frames;
using Pulse.Core.Settings;

namespace Pulse.App.Overlay;

/// <summary>
/// Decides when the overlay is visible and feeds it. Text refreshes on the host's 4 Hz tick; the graph has its own
/// ~30 Hz timer that only runs while the overlay is showing a graph.
/// </summary>
internal sealed class OverlayController : IDisposable
{
    private static readonly OverlayMode[] Cycle = [OverlayMode.Strip, OverlayMode.Panel, OverlayMode.Detailed, OverlayMode.Off];

    private readonly PulseSettings _settings;
    private readonly TelemetryHub _hub;
    private readonly FramePipeline _pipeline;
    private readonly Action _persist;
    private readonly OverlayWindow _window = new();
    private readonly DispatcherTimer _graphTimer;
    private float[] _graphBuffer = [];
    private bool _shown;
    private bool _hidden;
    private int _ticks;

    public OverlayController(PulseSettings settings, TelemetryHub hub, FramePipeline pipeline, Action persist)
    {
        _settings = settings;
        _hub = hub;
        _pipeline = pipeline;
        _persist = persist;
        _graphTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _graphTimer.Tick += (_, _) => DrawGraph();
        ApplySettings();
    }

    public OverlayMode Mode => _settings.Overlay.Mode;

    public bool Hidden => _hidden;

    public event Action? StateChanged;

    public void CycleMode()
    {
        int i = Array.IndexOf(Cycle, Mode);
        SetMode(Cycle[(i + 1) % Cycle.Length]);
    }

    public void SetMode(OverlayMode mode)
    {
        _settings.Overlay.Mode = mode;
        _hidden = false;
        _persist();
        ApplySettings();
        Tick();
        StateChanged?.Invoke();
    }

    public void ToggleHidden()
    {
        _hidden = !_hidden;
        Tick();
        StateChanged?.Invoke();
    }

    public void ApplySettings()
    {
        var o = _settings.Overlay;
        _window.Corner = o.Corner;
        _window.OffsetX = o.OffsetX;
        _window.OffsetY = o.OffsetY;
        _window.Visual.Configure(o.Mode, Math.Clamp(o.Scale, 0.6, 2.5), Math.Clamp(o.BackgroundOpacity, 0, 1), o.GraphFrames);
        _window.Visual.Update(BuildModel());
        _window.FitToContent(); // scale may change without the unscaled size changing
        if (_graphBuffer.Length != o.GraphFrames)
            _graphBuffer = new float[Math.Clamp(o.GraphFrames, 30, 2000)];
        if (_shown)
            _window.Reposition(force: true);
    }

    /// <summary>Called by the host at 4 Hz on the UI thread.</summary>
    public void Tick()
    {
        bool want = Mode != OverlayMode.Off && !_hidden && (!_settings.Overlay.OnlyWhenGameDetected || GameInView());
        if (want != _shown)
        {
            _shown = want;
            Log.Info(want ? $"Overlay shown ({Mode})" : "Overlay hidden");
            if (want)
            {
                _window.Visual.Update(BuildModel());
                _window.Show();
                _window.Reposition(force: true);
            }
            else
            {
                _window.Hide();
            }
        }

        bool graph = _shown && Mode is OverlayMode.Panel or OverlayMode.Detailed;
        if (graph != _graphTimer.IsEnabled)
        {
            if (graph)
                _graphTimer.Start();
            else
                _graphTimer.Stop();
        }

        if (!_shown)
            return;

        _window.AnchorHwnd = _hub.Game?.Hwnd ?? 0;
        _window.Visual.Update(BuildModel());
        _window.Reposition();
        if (++_ticks % 8 == 0)
            _window.ReassertTopmost();
    }

    /// <summary>
    /// The game stays "detected" through alt-tabs, but the overlay should only sit on top while the game is what you're
    /// looking at: the game has focus, or the focused app is on another monitor and the game isn't minimized.
    /// </summary>
    private bool GameInView()
    {
        if (_hub.Game is not { } game)
            return false;
        if (_hub.ForegroundPid == game.ProcessId)
            return true;
        if (game.Hwnd == 0 || !Win32.IsWindow(game.Hwnd) || Win32.IsIconic(game.Hwnd))
            return false;
        return Win32.MonitorFromWindow(_hub.ForegroundHwnd, Win32.MONITOR_DEFAULTTONEAREST) !=
               Win32.MonitorFromWindow(game.Hwnd, Win32.MONITOR_DEFAULTTONEAREST);
    }

    /// <summary>Replays per minute come from the host, which owns the NVML counter history.</summary>
    public Func<long> PcieReplaysRecent { get; set; } = () => 0;

    private OverlayModel BuildModel()
    {
        var game = _hub.Game;
        var frames = _hub.Frames;
        return new OverlayModel(
            Frames: game is not null && frames?.ProcessId == game.ProcessId ? frames : null,
            Hardware: _hub.Hardware,
            Pcie: _hub.Pcie,
            PcieReplaysRecent: PcieReplaysRecent(),
            GameName: game is null ? null : System.IO.Path.GetFileNameWithoutExtension(game.ExeName),
            GameTime: game is null ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Math.Max(0, AppClock.NowMs - game.StartedMs)),
            Alerts: _hub.Alerts,
            CpuHotC: _settings.Alerts.CpuTempC,
            GpuHotC: _settings.Alerts.GpuTempC);
    }

    private void DrawGraph()
    {
        int n = _pipeline.CopyRecentFrameTimes(_graphBuffer);
        _window.Visual.UpdateGraph(_graphBuffer.AsSpan(0, n), _hub.Frames?.StutterThresholdMs ?? double.NaN);
    }

    public void Dispose()
    {
        _graphTimer.Stop();
        _window.Close();
    }
}
