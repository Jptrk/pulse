namespace Pulse.Core.Stats;

/// <summary>
/// Flags frames that take much longer than the recent norm.
/// A stutter is a frame time above max(ratio × rolling median, median + minExtraMs).
/// The median is recomputed every few frames to keep per-frame cost flat.
/// </summary>
public sealed class StutterDetector
{
    private const int MinSamples = 30;

    private readonly float[] _window;
    private readonly float[] _scratch;
    private readonly double _ratio;
    private readonly double _minExtraMs;
    private readonly int _recomputeEvery;
    private int _next;
    private int _filled;
    private int _sinceRecompute;

    public StutterDetector(int window = 120, double ratio = 2.0, double minExtraMs = 8.0, int recomputeEvery = 8)
    {
        _window = new float[window];
        _scratch = new float[window];
        _ratio = ratio;
        _minExtraMs = minExtraMs;
        _recomputeEvery = recomputeEvery;
    }

    public double MedianMs { get; private set; } = double.NaN;

    public double ThresholdMs => double.IsNaN(MedianMs) ? double.NaN : Math.Max(MedianMs * _ratio, MedianMs + _minExtraMs);

    /// <summary>Adds a frame time and returns true if it is a stutter relative to frames before it.</summary>
    public bool Push(float frameTimeMs)
    {
        bool stutter = _filled >= MinSamples && frameTimeMs > ThresholdMs;

        _window[_next] = frameTimeMs;
        _next = (_next + 1) % _window.Length;
        if (_filled < _window.Length)
            _filled++;

        if (++_sinceRecompute >= _recomputeEvery || _filled == MinSamples)
        {
            _sinceRecompute = 0;
            MedianMs = FrameStats.Median(_window.AsSpan(0, _filled), _scratch);
        }

        return stutter;
    }

    public void Reset()
    {
        _next = 0;
        _filled = 0;
        _sinceRecompute = 0;
        MedianMs = double.NaN;
    }
}
