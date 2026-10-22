using Pulse.Core.Analysis;
using Pulse.Core.Frames;
using Pulse.Core.Sessions;
using Pulse.Core.Settings;
using Pulse.Core.Stats;

namespace Pulse.Core.Tests;

public class FramePipelineTests
{
    private static PresentMonRow Row(int pid, string app, double t, float ft, float gpuBusy = float.NaN) =>
        new(app, pid, "Hardware: Independent Flip", new FrameSample(t, ft, ft, 0, gpuBusy, 0, ft, 5, float.NaN, float.NaN));

    [Fact]
    public void Tracks_present_rate_per_process()
    {
        var p = new FramePipeline(1024);
        for (int i = 0; i <= 200; i++)
            p.Ingest(Row(1, "a.exe", i * 10, 10), nowMs: 1000);
        for (int i = 0; i <= 20; i++)
            p.Ingest(Row(2, "b.exe", i * 100, 100), nowMs: 1000);

        var list = p.GetPresentingProcesses(nowMs: 1500);
        Assert.InRange(list.Single(x => x.ProcessId == 1).PresentRate, 99, 101);
        Assert.InRange(list.Single(x => x.ProcessId == 2).PresentRate, 9.9, 10.1);
    }

    [Fact]
    public void Stale_processes_report_zero_rate_then_drop()
    {
        var p = new FramePipeline(1024);
        for (int i = 0; i <= 200; i++)
            p.Ingest(Row(1, "a.exe", i * 10, 10), nowMs: 0);
        Assert.Equal(0, p.GetPresentingProcesses(nowMs: 2000).Single().PresentRate);
        Assert.Empty(p.GetPresentingProcesses(nowMs: 5000));
    }

    [Fact]
    public void Only_active_process_feeds_stats_and_event()
    {
        var p = new FramePipeline(4096);
        int events = 0;
        p.ActiveFrame += (_, _, _) => events++;
        p.SetActiveProcess(1);

        for (int i = 0; i < 500; i++)
        {
            p.Ingest(Row(1, "game.exe", i * 5, 5, gpuBusy: 4.8f), 0);
            p.Ingest(Row(2, "other.exe", i * 5, 50), 0);
        }

        var s = p.ComputeStats(30_000)!;
        Assert.Equal(500, events);
        Assert.Equal(500, s.Window.Frames);
        Assert.Equal(200, s.Window.AvgFps, 3);
        Assert.Equal(200, s.CurrentFps, 3);
        Assert.Equal(Bottleneck.Gpu, s.Bottleneck);
        Assert.Equal(5, s.DisplayLatencyMs, 3);
        Assert.True(double.IsNaN(s.PcLatencyMs));
    }

    [Fact]
    public void Window_limits_stats_to_recent_frames()
    {
        var p = new FramePipeline(8192);
        p.SetActiveProcess(1);
        double t = 0;
        for (int i = 0; i < 1000; i++, t += 20)
            p.Ingest(Row(1, "g.exe", t, 20), 0); // 20 s at 50 fps
        for (int i = 0; i < 1000; i++, t += 10)
            p.Ingest(Row(1, "g.exe", t, 10), 0); // 10 s at 100 fps

        Assert.Equal(100, p.ComputeStats(5_000)!.Window.AvgFps, 1);
    }

    [Fact]
    public void Switching_active_process_resets_history()
    {
        var p = new FramePipeline(1024);
        p.SetActiveProcess(1);
        for (int i = 0; i < 100; i++)
            p.Ingest(Row(1, "g.exe", i * 10, 10), 0);
        p.SetActiveProcess(2);
        Assert.Equal(0, p.ComputeStats(30_000)!.Window.Frames);
        Assert.Equal(0, p.CopyRecentFrameTimes(new float[100]));
    }

    [Fact]
    public void Capture_restart_resets_timeline()
    {
        var p = new FramePipeline(4096);
        p.SetActiveProcess(1);
        for (int i = 0; i < 2000; i++)
            p.Ingest(Row(1, "g.exe", 60_000 + i * 20, 20, gpuBusy: 19.5f), 0); // long first run at 50 fps

        p.ResetTimeline(); // PresentMon restarted; its clock starts at 0 again
        for (int i = 0; i < 500; i++)
            p.Ingest(Row(1, "g.exe", i * 5, 5, gpuBusy: 1f), 0); // 200 fps, CPU-bound

        var s = p.ComputeStats(30_000)!;
        Assert.Equal(500, s.Window.Frames);
        Assert.Equal(200, s.CurrentFps, 3);
        Assert.Equal(Bottleneck.Cpu, s.Bottleneck);
    }

    [Fact]
    public void Graph_copy_returns_newest_oldest_first()
    {
        var p = new FramePipeline(1024);
        p.SetActiveProcess(1);
        for (int i = 1; i <= 10; i++)
            p.Ingest(Row(1, "g.exe", i * 10, i), 0);
        var buf = new float[3];
        Assert.Equal(3, p.CopyRecentFrameTimes(buf));
        Assert.Equal([8f, 9f, 10f], buf);
    }

    [Fact]
    public void Real_capture_stats_match_offline_computation()
    {
        string[] lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "desktop-apps-v2.csv"));
        var parser = new PresentMonCsvParser(lines[0]);
        var p = new FramePipeline();
        var rows = lines.Skip(1).Select(l => parser.TryParse(l, out var r) ? r : default).ToList();
        int pid = rows.First(r => r.Application == "WindowsTerminal.exe").ProcessId;
        p.SetActiveProcess(pid);
        foreach (var r in rows)
            p.Ingest(r, 0);

        float[] expected = rows.Where(r => r.ProcessId == pid).Select(r => r.Frame.FrameTimeMs).ToArray();
        var offline = FrameStats.Compute(expected);
        var live = p.ComputeStats(60_000)!.Window;
        Assert.Equal(offline.Frames, live.Frames);
        Assert.Equal(offline.AvgFps, live.AvgFps, 6);
        Assert.Equal(offline.Low1Fps, live.Low1Fps, 6);
    }
}

public class SessionAccumulatorTests
{
    private static SensorSample Sample(double cpuTemp, double gpuTemp, double gen, bool loaded, double gpuLoad = 90) =>
        new(0, 100, 40, cpuTemp, 80, gpuLoad, gpuTemp, 250, 2700, 8000, 14, gen, 16, loaded);

    [Fact]
    public void Summarizes_frames_and_sensors()
    {
        var start = new DateTime(2026, 10, 22, 20, 0, 0, DateTimeKind.Utc);
        var acc = new SessionAccumulator("game.exe", start);
        for (int i = 0; i < 990; i++)
            acc.AddFrame(10, false, Bottleneck.Gpu);
        for (int i = 0; i < 10; i++)
            acc.AddFrame(40, true, Bottleneck.Cpu);

        acc.AddSample(Sample(70, 60, 4, loaded: true));
        acc.AddSample(Sample(75, 66, 1, loaded: false)); // idle link downclock ignored
        acc.AddSample(Sample(72, 64, 4, loaded: true));
        acc.PcieReplays = 3;

        var s = acc.Summarize(start.AddMinutes(10));
        Assert.Equal(600, s.DurationSeconds);
        Assert.Equal(1000, s.Frames);
        Assert.Equal(25, s.Low1Fps, 6);
        Assert.Equal(10, s.Stutters);
        Assert.Equal(75, s.MaxCpuTempC);
        Assert.Equal(66, s.MaxGpuTempC);
        Assert.Equal(4, s.MinPcieGen);
        Assert.Equal(3, s.PcieReplays);
        Assert.Equal(100.0 * 9900 / 10300, s.PctGpuBound, 6);
    }

    [Fact]
    public void Unflushed_frames_are_returned_once()
    {
        var acc = new SessionAccumulator("g.exe", DateTime.UtcNow);
        acc.AddFrame(1, false, Bottleneck.Unknown);
        acc.AddFrame(2, false, Bottleneck.Unknown);
        Assert.Equal([1f, 2f], acc.TakeUnflushedFrames());
        acc.AddFrame(3, false, Bottleneck.Unknown);
        Assert.Equal([3f], acc.TakeUnflushedFrames());
        Assert.Empty(acc.TakeUnflushedFrames());
    }
}

public class SettingsTests
{
    [Fact]
    public void Allowlist_overrides_default_deny()
    {
        var s = new PulseSettings();
        s.Games.Allow.Add("chrome.exe");
        s.Games.Deny.Add("blender.exe");
        var o = s.ToDetectorOptions();
        Assert.DoesNotContain("chrome.exe", o.Deny);
        Assert.Contains("CHROME.EXE", o.Allow);
        Assert.Contains("Blender.exe", o.Deny);
        Assert.Contains("explorer.exe", o.Deny);
    }
}
