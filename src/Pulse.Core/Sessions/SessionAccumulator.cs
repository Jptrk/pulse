using Pulse.Core.Analysis;
using Pulse.Core.Stats;

namespace Pulse.Core.Sessions;

/// <summary>One 1 Hz telemetry sample during a session. NaN = not available.</summary>
public sealed record SensorSample(
    double ElapsedSeconds,
    double Fps,
    double CpuLoad,
    double CpuTempC,
    double CpuPowerW,
    double GpuLoad,
    double GpuTempC,
    double GpuPowerW,
    double GpuClockMhz,
    double VramUsedMb,
    double RamUsedGb,
    double PcieGen,
    double PcieWidth,
    bool GpuUnderLoad);

public sealed record SessionSummary(
    string Exe,
    DateTime StartedUtc,
    DateTime EndedUtc,
    double DurationSeconds,
    int Frames,
    double AvgFps,
    double Low1Fps,
    double Low01Fps,
    double P99FrameTimeMs,
    long Stutters,
    double MaxCpuTempC,
    double MaxGpuTempC,
    double MaxGpuPowerW,
    double MaxVramMb,
    double AvgCpuLoad,
    double AvgGpuLoad,
    double MinPcieGen,
    double MinPcieWidth,
    long PcieReplays,
    long WheaEvents,
    double PctGpuBound,
    double PctCpuBound);

/// <summary>
/// Collects everything about one game session and produces its summary. Not thread-safe; callers lock.
/// Frame times are kept in full so the summary uses the exact same math as the overlay.
/// </summary>
public sealed class SessionAccumulator(string exe, DateTime startedUtc)
{
    private readonly List<float> _frameTimes = new(64 * 1024);
    private readonly double[] _boundTime = new double[Enum.GetValues<Bottleneck>().Length];
    private int _flushedFrames;
    private long _stutters;
    private int _samples;
    private double _cpuLoadSum, _gpuLoadSum;
    private int _cpuLoadN, _gpuLoadN;
    private double _maxCpuTemp = double.NaN, _maxGpuTemp = double.NaN, _maxGpuPower = double.NaN, _maxVram = double.NaN;
    private double _minPcieGen = double.NaN, _minPcieWidth = double.NaN;

    public string Exe { get; } = exe;
    public DateTime StartedUtc { get; } = startedUtc;
    public int FrameCount => _frameTimes.Count;
    public int SampleCount => _samples;
    public long PcieReplays { get; set; }
    public long WheaEvents { get; set; }

    public void AddFrame(float frameTimeMs, bool stutter, Bottleneck bottleneck)
    {
        _frameTimes.Add(frameTimeMs);
        if (stutter)
            _stutters++;
        _boundTime[(int)bottleneck] += frameTimeMs;
    }

    public void AddSample(SensorSample s)
    {
        _samples++;
        Accumulate(ref _cpuLoadSum, ref _cpuLoadN, s.CpuLoad);
        Accumulate(ref _gpuLoadSum, ref _gpuLoadN, s.GpuLoad);
        _maxCpuTemp = Max(_maxCpuTemp, s.CpuTempC);
        _maxGpuTemp = Max(_maxGpuTemp, s.GpuTempC);
        _maxGpuPower = Max(_maxGpuPower, s.GpuPowerW);
        _maxVram = Max(_maxVram, s.VramUsedMb);
        if (s.GpuUnderLoad)
        {
            _minPcieGen = Min(_minPcieGen, s.PcieGen);
            _minPcieWidth = Min(_minPcieWidth, s.PcieWidth);
        }
    }

    /// <summary>Frame times added since the previous call, for incremental storage.</summary>
    public float[] TakeUnflushedFrames()
    {
        float[] chunk = _frameTimes.GetRange(_flushedFrames, _frameTimes.Count - _flushedFrames).ToArray();
        _flushedFrames = _frameTimes.Count;
        return chunk;
    }

    public SessionSummary Summarize(DateTime endedUtc)
    {
        FrameStatsResult stats = FrameStats.Compute(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_frameTimes));
        double totalBound = _boundTime.Sum();

        return new SessionSummary(
            Exe,
            StartedUtc,
            endedUtc,
            DurationSeconds: (endedUtc - StartedUtc).TotalSeconds,
            Frames: stats.Frames,
            AvgFps: stats.AvgFps,
            Low1Fps: stats.Low1Fps,
            Low01Fps: stats.Low01Fps,
            P99FrameTimeMs: stats.P99FrameTimeMs,
            Stutters: _stutters,
            MaxCpuTempC: _maxCpuTemp,
            MaxGpuTempC: _maxGpuTemp,
            MaxGpuPowerW: _maxGpuPower,
            MaxVramMb: _maxVram,
            AvgCpuLoad: _cpuLoadN > 0 ? _cpuLoadSum / _cpuLoadN : double.NaN,
            AvgGpuLoad: _gpuLoadN > 0 ? _gpuLoadSum / _gpuLoadN : double.NaN,
            MinPcieGen: _minPcieGen,
            MinPcieWidth: _minPcieWidth,
            PcieReplays: PcieReplays,
            WheaEvents: WheaEvents,
            PctGpuBound: totalBound > 0 ? 100 * _boundTime[(int)Bottleneck.Gpu] / totalBound : double.NaN,
            PctCpuBound: totalBound > 0 ? 100 * _boundTime[(int)Bottleneck.Cpu] / totalBound : double.NaN);
    }

    private static void Accumulate(ref double sum, ref int n, double v)
    {
        if (double.IsNaN(v))
            return;
        sum += v;
        n++;
    }

    private static double Max(double current, double v) => double.IsNaN(v) ? current : double.IsNaN(current) ? v : Math.Max(current, v);

    private static double Min(double current, double v) => double.IsNaN(v) || v <= 0 ? current : double.IsNaN(current) ? v : Math.Min(current, v);
}
