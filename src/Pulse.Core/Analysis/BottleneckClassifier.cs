using Pulse.Core.Frames;
using Pulse.Core.Stats;

namespace Pulse.Core.Analysis;

public enum Bottleneck
{
    Unknown,
    Gpu,
    Cpu,
    /// <summary>Frame rate held back by a limiter or V-Sync: the app spends a large share of each frame waiting.</summary>
    Capped,
    Balanced,
}

public sealed record BottleneckThresholds(
    double GpuBoundRatio = 0.85,
    double CpuBoundGpuRatio = 0.70,
    double CappedWaitRatio = 0.25,
    double WindowMs = 1000,
    double HoldMs = 2000,
    double EvaluateEveryMs = 250);

/// <summary>
/// Classifies what limits the frame rate from PresentMon's GPUBusy/CPUWait vs frame time.
/// Uses medians over a short window, and a new verdict must hold for <see cref="BottleneckThresholds.HoldMs"/>
/// before it replaces the current one, so the label doesn't flicker.
/// </summary>
public sealed class BottleneckClassifier
{
    private readonly BottleneckThresholds _t;
    private readonly Queue<(double T, float GpuRatio, float WaitRatio)> _window = new();
    private float[] _scratchA = new float[256];
    private float[] _scratchB = new float[256];
    private double _lastEvaluate = double.NegativeInfinity;
    private Bottleneck _pending = Bottleneck.Unknown;
    private double _pendingSince;

    public BottleneckClassifier(BottleneckThresholds? thresholds = null) => _t = thresholds ?? new BottleneckThresholds();

    public Bottleneck Current { get; private set; } = Bottleneck.Unknown;

    /// <summary>Median GPUBusy / frame time over the window, 0..1. NaN if PresentMon doesn't report GPUBusy.</summary>
    public double GpuBusyRatio { get; private set; } = double.NaN;

    public void Push(in FrameSample frame)
    {
        if (double.IsNaN(frame.CpuStartMs) || float.IsNaN(frame.GpuBusyMs) || !(frame.FrameTimeMs > 0))
            return;

        double t = frame.CpuStartMs;
        if (_window.Count > 0 && t < _window.Peek().T)
            Reset(); // capture timeline restarted
        float wait = float.IsNaN(frame.CpuWaitMs) ? 0f : frame.CpuWaitMs;
        _window.Enqueue((t, Math.Clamp(frame.GpuBusyMs / frame.FrameTimeMs, 0f, 1.5f), Math.Clamp(wait / frame.FrameTimeMs, 0f, 1f)));
        while (_window.Count > 0 && t - _window.Peek().T > _t.WindowMs)
            _window.Dequeue();

        if (t - _lastEvaluate < _t.EvaluateEveryMs)
            return;
        _lastEvaluate = t;
        Evaluate(t);
    }

    private void Evaluate(double now)
    {
        int n = _window.Count;
        if (n < 5)
            return;
        if (_scratchA.Length < n)
        {
            _scratchA = new float[n * 2];
            _scratchB = new float[n * 2];
        }

        int i = 0;
        foreach (var (_, gpu, wait) in _window)
        {
            _scratchA[i] = gpu;
            _scratchB[i] = wait;
            i++;
        }

        double gpuRatio = FrameStats.Median(_scratchA.AsSpan(0, n), _scratchA);
        double waitRatio = FrameStats.Median(_scratchB.AsSpan(0, n), _scratchB);
        GpuBusyRatio = Math.Min(gpuRatio, 1.0);

        Bottleneck verdict = Classify(gpuRatio, waitRatio, _t);
        if (Current == Bottleneck.Unknown)
        {
            Current = verdict;
            _pending = verdict;
            return;
        }
        if (verdict == Current)
        {
            _pending = Current;
            return;
        }
        if (verdict != _pending)
        {
            _pending = verdict;
            _pendingSince = now;
            return;
        }
        if (now - _pendingSince >= _t.HoldMs)
            Current = verdict;
    }

    public static Bottleneck Classify(double gpuRatio, double waitRatio, BottleneckThresholds t)
    {
        if (double.IsNaN(gpuRatio))
            return Bottleneck.Unknown;
        if (gpuRatio >= t.GpuBoundRatio)
            return Bottleneck.Gpu;
        if (waitRatio >= t.CappedWaitRatio)
            return Bottleneck.Capped;
        if (gpuRatio < t.CpuBoundGpuRatio)
            return Bottleneck.Cpu;
        return Bottleneck.Balanced;
    }

    public void Reset()
    {
        _window.Clear();
        _lastEvaluate = double.NegativeInfinity;
        _pending = Bottleneck.Unknown;
        Current = Bottleneck.Unknown;
        GpuBusyRatio = double.NaN;
    }
}
