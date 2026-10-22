using Pulse.Core.Analysis;
using Pulse.Core.Stats;

namespace Pulse.Core.Frames;

/// <summary>Frame statistics for the active game over a trailing window, plus "right now" values.</summary>
public sealed record FrameWindowStats(
    int ProcessId,
    FrameStatsResult Window,
    double CurrentFps,
    double CurrentFrameTimeMs,
    Bottleneck Bottleneck,
    double GpuBusyRatio,
    double StutterThresholdMs,
    long StuttersTotal,
    double DisplayLatencyMs,
    double PcLatencyMs,
    double ClickToPhotonMs,
    string PresentMode);

/// <summary>
/// Consumes every PresentMon row. Tracks present rates for all processes (for game detection)
/// and keeps full frame history plus live analysis for the one active process.
/// <see cref="Ingest"/> runs on the capture thread; everything else may be called from any thread.
/// </summary>
public sealed class FramePipeline
{
    private sealed class ProcessTracker
    {
        public string Application = "";
        public string PresentMode = "";
        public double LastSeenMs;
        public double BucketStartPm = double.NaN;
        public int BucketCount;
        public double Rate;
    }

    private readonly Lock _lock = new();
    private readonly Dictionary<int, ProcessTracker> _processes = [];
    private readonly FrameRing _ring;
    private readonly StutterDetector _stutter = new();
    private readonly BottleneckClassifier _bottleneck;
    private readonly FrameSample[] _statsFrames;
    private readonly float[] _statsTimes;
    private readonly float[] _statsScratch;
    private int _activePid;
    private long _stutters;
    private string _activeMode = "";

    public FramePipeline(int ringCapacity = 32768, BottleneckThresholds? thresholds = null)
    {
        _ring = new FrameRing(ringCapacity);
        _bottleneck = new BottleneckClassifier(thresholds);
        _statsFrames = new FrameSample[ringCapacity];
        _statsTimes = new float[ringCapacity];
        _statsScratch = new float[ringCapacity];
    }

    /// <summary>Raised on the capture thread for each frame of the active process, with its stutter flag and current bottleneck.</summary>
    public event Action<FrameSample, bool, Bottleneck>? ActiveFrame;

    public int ActiveProcessId
    {
        get { lock (_lock) return _activePid; }
    }

    public void SetActiveProcess(int pid)
    {
        lock (_lock)
        {
            if (_activePid == pid)
                return;
            _activePid = pid;
            _ring.Clear();
            _stutter.Reset();
            _bottleneck.Reset();
            _stutters = 0;
            _activeMode = "";
        }
    }

    /// <summary>
    /// Call when PresentMon restarts: its timestamps count from capture start, so history from the previous run
    /// would otherwise sit "in the future" and break windowing. Keeps the active process.
    /// </summary>
    public void ResetTimeline()
    {
        lock (_lock)
        {
            _ring.Clear();
            _stutter.Reset();
            _bottleneck.Reset();
            foreach (var p in _processes.Values)
            {
                p.BucketStartPm = double.NaN;
                p.BucketCount = 0;
            }
        }
    }

    public void Ingest(in PresentMonRow row, double nowMs)
    {
        bool isActive;
        bool stutter = false;
        Bottleneck bottleneck = Bottleneck.Unknown;

        lock (_lock)
        {
            if (!_processes.TryGetValue(row.ProcessId, out var p))
            {
                p = new ProcessTracker();
                _processes[row.ProcessId] = p;
            }
            p.Application = row.Application;
            p.PresentMode = row.PresentMode;
            p.LastSeenMs = nowMs;
            UpdateRate(p, row.Frame);

            isActive = row.ProcessId == _activePid;
            if (isActive)
            {
                _ring.Add(row.Frame);
                stutter = _stutter.Push(row.Frame.FrameTimeMs);
                if (stutter)
                    _stutters++;
                _bottleneck.Push(row.Frame);
                bottleneck = _bottleneck.Current;
                _activeMode = row.PresentMode;
            }
        }

        if (isActive)
            ActiveFrame?.Invoke(row.Frame, stutter, bottleneck);
    }

    // Rate is counted on PresentMon's own timeline, which stays accurate even when stdout arrives in bursts.
    private static void UpdateRate(ProcessTracker p, in FrameSample frame)
    {
        double t = frame.CpuStartMs;
        if (double.IsNaN(t))
            return;
        if (double.IsNaN(p.BucketStartPm) || t < p.BucketStartPm)
        {
            p.BucketStartPm = t;
            p.BucketCount = 0;
        }
        p.BucketCount++;
        double elapsed = t - p.BucketStartPm;
        if (elapsed >= 1000)
        {
            p.Rate = p.BucketCount * 1000.0 / elapsed;
            p.BucketStartPm = t;
            p.BucketCount = 0;
        }
    }

    /// <summary>Processes seen presenting within <paramref name="maxAgeMs"/>; stale entries are dropped.</summary>
    public List<ProcessPresentInfo> GetPresentingProcesses(double nowMs, double maxAgeMs = 3000)
    {
        lock (_lock)
        {
            var result = new List<ProcessPresentInfo>(_processes.Count);
            List<int>? stale = null;
            foreach (var (pid, p) in _processes)
            {
                double age = nowMs - p.LastSeenMs;
                if (age > 60_000 && pid != _activePid)
                {
                    (stale ??= []).Add(pid);
                    continue;
                }
                if (age <= maxAgeMs)
                    result.Add(new ProcessPresentInfo(pid, p.Application, age > 1500 ? 0 : p.Rate, p.PresentMode, p.LastSeenMs));
            }
            if (stale is not null)
            {
                foreach (int pid in stale)
                    _processes.Remove(pid);
            }
            return result;
        }
    }

    public FrameWindowStats? ComputeStats(double windowMs)
    {
        lock (_lock)
        {
            if (_activePid == 0)
                return null;

            int n = _ring.CopyWindow(windowMs, _statsFrames);
            if (n == 0)
                return new FrameWindowStats(_activePid, FrameStatsResult.Empty, double.NaN, double.NaN, Bottleneck.Unknown,
                    double.NaN, double.NaN, _stutters, double.NaN, double.NaN, double.NaN, _activeMode);

            for (int i = 0; i < n; i++)
                _statsTimes[i] = _statsFrames[i].FrameTimeMs;
            FrameStatsResult window = FrameStats.Compute(_statsTimes.AsSpan(0, n), _statsScratch);

            // "Current" values use the last second, like a classic FPS counter.
            double newest = _statsFrames[n - 1].CpuStartMs;
            int first = n - 1;
            while (first > 0 && !double.IsNaN(newest) && newest - _statsFrames[first - 1].CpuStartMs <= 1000)
                first--;
            int recent = n - first;
            double recentSum = 0;
            for (int i = first; i < n; i++)
                recentSum += _statsFrames[i].FrameTimeMs;

            return new FrameWindowStats(
                ProcessId: _activePid,
                Window: window,
                CurrentFps: recentSum > 0 ? 1000.0 * recent / recentSum : double.NaN,
                CurrentFrameTimeMs: recent > 0 ? recentSum / recent : double.NaN,
                Bottleneck: _bottleneck.Current,
                GpuBusyRatio: _bottleneck.GpuBusyRatio,
                StutterThresholdMs: _stutter.ThresholdMs,
                StuttersTotal: _stutters,
                DisplayLatencyMs: MedianOf(first, n, static f => f.DisplayLatencyMs),
                PcLatencyMs: MedianOf(first, n, static f => f.PcLatencyMs),
                ClickToPhotonMs: MedianOf(first, n, static f => f.ClickToPhotonMs),
                PresentMode: _activeMode);
        }
    }

    private double MedianOf(int from, int to, Func<FrameSample, float> selector)
    {
        for (int i = from; i < to; i++)
            _statsTimes[i - from] = selector(_statsFrames[i]);
        return FrameStats.Median(_statsTimes.AsSpan(0, to - from), _statsScratch);
    }

    /// <summary>Newest frame times of the active process, oldest first, for the live graph.</summary>
    public int CopyRecentFrameTimes(Span<float> destination)
    {
        Span<FrameSample> frames = destination.Length <= 512 ? stackalloc FrameSample[destination.Length] : new FrameSample[destination.Length];
        int n = _ring.CopyLast(frames);
        for (int i = 0; i < n; i++)
            destination[i] = frames[i].FrameTimeMs;
        return n;
    }
}
