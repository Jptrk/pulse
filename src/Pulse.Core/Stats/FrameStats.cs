namespace Pulse.Core.Stats;

public readonly record struct FrameStatsResult(
    int Frames,
    double AvgFps,
    double Low1Fps,
    double Low01Fps,
    double AvgFrameTimeMs,
    double P99FrameTimeMs,
    double MaxFrameTimeMs)
{
    public static readonly FrameStatsResult Empty = new(0, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
}

/// <summary>
/// Frame-rate statistics over a set of frame times.
/// Average FPS is time-weighted (frames / total time), not the mean of per-frame FPS.
/// "1% low" is 1000 / mean of the slowest 1% of frame times; "0.1% low" likewise with 0.1%.
/// Lows are NaN until there are enough frames for the slowest slice to mean something.
/// </summary>
public static class FrameStats
{
    public const int MinFramesFor1Low = 100;
    public const int MinFramesFor01Low = 1000;

    public static FrameStatsResult Compute(ReadOnlySpan<float> frameTimesMs)
    {
        float[] scratch = new float[frameTimesMs.Length];
        return Compute(frameTimesMs, scratch);
    }

    /// <param name="scratch">Reusable buffer at least as long as <paramref name="frameTimesMs"/>; avoids allocating per call.</param>
    public static FrameStatsResult Compute(ReadOnlySpan<float> frameTimesMs, Span<float> scratch)
    {
        if (scratch.Length < frameTimesMs.Length)
            throw new ArgumentException("Scratch buffer too small.", nameof(scratch));

        int n = 0;
        double sum = 0;
        foreach (float ft in frameTimesMs)
        {
            if (!(ft > 0) || float.IsInfinity(ft))
                continue;
            scratch[n++] = ft;
            sum += ft;
        }

        if (n == 0)
            return FrameStatsResult.Empty;

        Span<float> sorted = scratch[..n];
        sorted.Sort();

        double avgFrameTime = sum / n;
        int p99Index = Math.Max(0, (int)Math.Ceiling(n * 0.99) - 1);

        return new FrameStatsResult(
            Frames: n,
            AvgFps: 1000.0 * n / sum,
            Low1Fps: n >= MinFramesFor1Low ? LowFps(sorted, 0.01) : double.NaN,
            Low01Fps: n >= MinFramesFor01Low ? LowFps(sorted, 0.001) : double.NaN,
            AvgFrameTimeMs: avgFrameTime,
            P99FrameTimeMs: sorted[p99Index],
            MaxFrameTimeMs: sorted[n - 1]);
    }

    /// <summary>1000 / mean of the slowest <paramref name="fraction"/> of an ascending-sorted frame-time span.</summary>
    private static double LowFps(ReadOnlySpan<float> sortedAscending, double fraction)
    {
        int k = Math.Max(1, (int)Math.Ceiling(sortedAscending.Length * fraction));
        double sum = 0;
        for (int i = sortedAscending.Length - k; i < sortedAscending.Length; i++)
            sum += sortedAscending[i];
        return 1000.0 / (sum / k);
    }

    /// <summary>Median of a span; reorders <paramref name="scratch"/>. NaN for an empty input.</summary>
    public static double Median(ReadOnlySpan<float> values, Span<float> scratch)
    {
        int n = 0;
        foreach (float v in values)
        {
            if (!float.IsNaN(v))
                scratch[n++] = v;
        }
        if (n == 0)
            return double.NaN;

        Span<float> s = scratch[..n];
        s.Sort();
        return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2.0;
    }
}
