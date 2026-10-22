using Pulse.Core.Analysis;

namespace Pulse.Core.Tests;

public class GameDetectorTests
{
    private const int Game = 100;
    private const int Browser = 200;

    private static ForegroundInfo Fg(int pid, string exe, bool fullscreen = true) => new(pid, exe, fullscreen, pid);

    private static ProcessPresentInfo Presenting(int pid, string exe, double rate, double lastSeen) =>
        new(pid, exe, rate, "Hardware: Independent Flip", lastSeen);

    private static GameDetector NewDetector(params string[] allow) => new(new GameDetectorOptions
    {
        Allow = new HashSet<string>(allow, StringComparer.OrdinalIgnoreCase),
    });

    [Fact]
    public void Fullscreen_presenting_foreground_app_becomes_game_after_confirm()
    {
        var d = NewDetector();
        var fg = Fg(Game, "Game.exe");

        Assert.Null(d.Update(0, fg, [Presenting(Game, "Game.exe", 144, 0)], _ => true).Started);
        Assert.Null(d.Update(2000, fg, [Presenting(Game, "Game.exe", 144, 2000)], _ => true).Started);
        var u = d.Update(3000, fg, [Presenting(Game, "Game.exe", 144, 3000)], _ => true);

        Assert.NotNull(u.Started);
        Assert.Equal(Game, d.Current!.ProcessId);
    }

    [Fact]
    public void Low_present_rate_is_not_a_game()
    {
        var d = NewDetector();
        var fg = Fg(Game, "Game.exe");
        d.Update(0, fg, [Presenting(Game, "Game.exe", 5, 0)], _ => true);
        d.Update(5000, fg, [Presenting(Game, "Game.exe", 5, 5000)], _ => true);
        Assert.Null(d.Current);
    }

    [Fact]
    public void Denied_exe_is_never_a_game()
    {
        var d = NewDetector();
        var fg = Fg(Browser, "chrome.exe");
        d.Update(0, fg, [Presenting(Browser, "chrome.exe", 144, 0)], _ => true);
        d.Update(5000, fg, [Presenting(Browser, "chrome.exe", 144, 5000)], _ => true);
        Assert.Null(d.Current);
    }

    [Fact]
    public void Windowed_needs_allowlist()
    {
        var plain = NewDetector();
        var allowed = NewDetector("Game.exe");
        var fg = Fg(Game, "game.exe", fullscreen: false);
        foreach (var d in new[] { plain, allowed })
        {
            d.Update(0, fg, [Presenting(Game, "game.exe", 60, 0)], _ => true);
            d.Update(3500, fg, [Presenting(Game, "game.exe", 60, 3500)], _ => true);
        }
        Assert.Null(plain.Current);
        Assert.NotNull(allowed.Current);
    }

    [Fact]
    public void Alt_tab_keeps_session_until_idle_timeout()
    {
        var d = NewDetector();
        var fg = Fg(Game, "Game.exe");
        d.Update(0, fg, [Presenting(Game, "Game.exe", 144, 0)], _ => true);
        d.Update(3000, fg, [Presenting(Game, "Game.exe", 144, 3000)], _ => true);
        Assert.NotNull(d.Current);

        // Alt-tab to browser; game minimised and stops presenting.
        var browser = Fg(Browser, "chrome.exe");
        d.Update(60_000, browser, [Presenting(Browser, "chrome.exe", 60, 60_000)], _ => true);
        Assert.NotNull(d.Current);

        var u = d.Update(124_000, browser, [], _ => true);
        Assert.Equal(Game, u.Ended!.ProcessId);
        Assert.Null(d.Current);
    }

    [Fact]
    public void Process_exit_ends_session()
    {
        var d = NewDetector();
        var fg = Fg(Game, "Game.exe");
        d.Update(0, fg, [Presenting(Game, "Game.exe", 144, 0)], _ => true);
        d.Update(3000, fg, [Presenting(Game, "Game.exe", 144, 3000)], _ => true);

        var u = d.Update(4000, Fg(0, ""), [], pid => pid != Game);
        Assert.NotNull(u.Ended);
        Assert.Null(d.Current);
    }

    [Fact]
    public void Another_game_takes_over()
    {
        var d = NewDetector();
        d.Update(0, Fg(Game, "A.exe"), [Presenting(Game, "A.exe", 144, 0)], _ => true);
        d.Update(3000, Fg(Game, "A.exe"), [Presenting(Game, "A.exe", 144, 3000)], _ => true);

        var b = Fg(300, "B.exe");
        d.Update(4000, b, [Presenting(300, "B.exe", 90, 4000)], _ => true);
        var u = d.Update(7000, b, [Presenting(300, "B.exe", 90, 7000)], _ => true);

        Assert.Equal(Game, u.Ended!.ProcessId);
        Assert.Equal(300, u.Started!.ProcessId);
    }

    [Fact]
    public void Reconfigure_keeps_current_game()
    {
        var d = NewDetector();
        var fg = Fg(Game, "Game.exe");
        d.Update(0, fg, [Presenting(Game, "Game.exe", 144, 0)], _ => true);
        d.Update(3000, fg, [Presenting(Game, "Game.exe", 144, 3000)], _ => true);

        d.Reconfigure(new GameDetectorOptions { MinPresentRate = 30 });
        var u = d.Update(3500, Fg(Browser, "chrome.exe"), [], _ => true);

        Assert.Null(u.Ended);
        Assert.Equal(Game, d.Current!.ProcessId);
    }

    [Fact]
    public void Candidate_must_stay_continuous()
    {
        var d = NewDetector();
        var fg = Fg(Game, "Game.exe");
        d.Update(0, fg, [Presenting(Game, "Game.exe", 144, 0)], _ => true);
        d.Update(1500, Fg(Browser, "chrome.exe"), [], _ => true); // focus lost resets confirmation
        d.Update(2000, fg, [Presenting(Game, "Game.exe", 144, 2000)], _ => true);
        d.Update(3500, fg, [Presenting(Game, "Game.exe", 144, 3500)], _ => true);
        Assert.Null(d.Current);
        d.Update(5000, fg, [Presenting(Game, "Game.exe", 144, 5000)], _ => true);
        Assert.NotNull(d.Current);
    }
}
