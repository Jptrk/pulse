namespace Pulse.Core.Analysis;

/// <summary>The foreground window as seen from outside the process (no handle to it is ever opened).</summary>
public readonly record struct ForegroundInfo(int ProcessId, string ExeName, bool CoversMonitor, nint Hwnd);

/// <summary>A process PresentMon has seen presenting frames recently.</summary>
public readonly record struct ProcessPresentInfo(int ProcessId, string Application, double PresentRate, string PresentMode, double LastSeenMs);

public sealed record DetectedGame(int ProcessId, string ExeName, nint Hwnd, double StartedMs)
{
    public double LastActivityMs { get; set; } = StartedMs;
    public nint Hwnd { get; set; } = Hwnd;
}

public readonly record struct GameDetectorUpdate(DetectedGame? Started, DetectedGame? Ended);

public sealed class GameDetectorOptions
{
    public double MinPresentRate { get; init; } = 20;
    public double ConfirmMs { get; init; } = 3000;
    public double IdleTimeoutMs { get; init; } = 120_000;
    public IReadOnlySet<string> Allow { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> Deny { get; init; } = DefaultDeny;

    /// <summary>Apps that present frames but are never games: shell, browsers, launchers, chat, streaming, wallpapers, tools.</summary>
    public static readonly IReadOnlySet<string> DefaultDeny = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "dwm.exe", "Pulse.exe", "Pulse.App.exe",
        "ApplicationFrameHost.exe", "SearchHost.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "TextInputHost.exe",
        "chrome.exe", "msedge.exe", "msedgewebview2.exe", "firefox.exe", "brave.exe", "opera.exe", "zen.exe",
        "Discord.exe", "Viber.exe", "Messenger.exe",
        "steam.exe", "steamwebhelper.exe", "EpicGamesLauncher.exe", "Battle.net.exe", "EADesktop.exe",
        "RiotClientServices.exe", "Riot Client.exe", "RiotClientUx.exe", "upc.exe", "UbisoftConnect.exe",
        "sunshine.exe", "Moonlight.exe", "obs64.exe", "vlc.exe",
        "WindowsTerminal.exe", "Code.exe", "claude.exe", "ChatGPT.exe",
        "webwallpaper64.exe", "wallpaper64.exe", "wallpaper32.exe",
        "SignalRgb.exe", "lghub.exe", "NVIDIA app.exe", "NVIDIA Overlay.exe",
    };
}

/// <summary>
/// Decides which process is "the game". A candidate must be in the foreground, present at least
/// <see cref="GameDetectorOptions.MinPresentRate"/> frames/s, cover its monitor (or be allow-listed),
/// and keep that up for <see cref="GameDetectorOptions.ConfirmMs"/>. Once detected, a game stays active through
/// alt-tabs until its process exits or it stops presenting for <see cref="GameDetectorOptions.IdleTimeoutMs"/>.
/// </summary>
public sealed class GameDetector(GameDetectorOptions options)
{
    private GameDetectorOptions _options = options;
    private int _candidatePid;
    private double _candidateSince;

    public DetectedGame? Current { get; private set; }

    /// <summary>Swaps allow/deny lists and thresholds without forgetting the game currently being tracked.</summary>
    public void Reconfigure(GameDetectorOptions newOptions) => _options = newOptions;

    public GameDetectorUpdate Update(double nowMs, ForegroundInfo foreground, IReadOnlyList<ProcessPresentInfo> presenting, Func<int, bool> isAlive)
    {
        DetectedGame? ended = null;
        DetectedGame? started = null;

        if (Current is { } game)
        {
            foreach (var p in presenting)
            {
                if (p.ProcessId == game.ProcessId)
                    game.LastActivityMs = Math.Max(game.LastActivityMs, p.LastSeenMs);
            }
            if (foreground.ProcessId == game.ProcessId && foreground.Hwnd != 0)
                game.Hwnd = foreground.Hwnd;

            if (!isAlive(game.ProcessId) || nowMs - game.LastActivityMs > _options.IdleTimeoutMs)
            {
                ended = game;
                Current = null;
            }
        }

        if (IsCandidate(foreground, presenting))
        {
            if (_candidatePid != foreground.ProcessId)
            {
                _candidatePid = foreground.ProcessId;
                _candidateSince = nowMs;
            }
            else if (nowMs - _candidateSince >= _options.ConfirmMs)
            {
                if (Current is not null)
                    ended = Current;
                Current = new DetectedGame(foreground.ProcessId, foreground.ExeName, foreground.Hwnd, nowMs);
                started = Current;
                _candidatePid = 0;
            }
        }
        else
        {
            _candidatePid = 0;
        }

        return new GameDetectorUpdate(started, ended);
    }

    private bool IsCandidate(ForegroundInfo fg, IReadOnlyList<ProcessPresentInfo> presenting)
    {
        if (fg.ProcessId == 0 || fg.ProcessId == Current?.ProcessId || string.IsNullOrEmpty(fg.ExeName))
            return false;
        if (_options.Deny.Contains(fg.ExeName))
            return false;
        if (!fg.CoversMonitor && !_options.Allow.Contains(fg.ExeName))
            return false;

        foreach (var p in presenting)
        {
            if (p.ProcessId == fg.ProcessId)
                return p.PresentRate >= _options.MinPresentRate;
        }
        return false;
    }
}
