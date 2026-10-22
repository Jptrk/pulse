using Pulse.Core.Analysis;
using Pulse.Core.Frames;

namespace Pulse.Core.Tests;

public class BottleneckClassifierTests
{
    private static readonly BottleneckThresholds T = new();

    [Theory]
    [InlineData(0.95, 0.0, Bottleneck.Gpu)]
    [InlineData(0.85, 0.5, Bottleneck.Gpu)]
    [InlineData(0.40, 0.60, Bottleneck.Capped)]
    [InlineData(0.40, 0.05, Bottleneck.Cpu)]
    [InlineData(0.78, 0.05, Bottleneck.Balanced)]
    [InlineData(double.NaN, 0.0, Bottleneck.Unknown)]
    public void Classify(double gpuRatio, double waitRatio, Bottleneck expected)
    {
        Assert.Equal(expected, BottleneckClassifier.Classify(gpuRatio, waitRatio, T));
    }

    private static FrameSample Frame(double t, float ft, float gpuBusy, float cpuWait = 0) =>
        new(t, ft, ft - cpuWait, cpuWait, gpuBusy, ft - gpuBusy, ft, float.NaN, float.NaN, float.NaN);

    private static double Feed(BottleneckClassifier c, double start, double durationMs, float ft, float gpuBusy, float cpuWait = 0)
    {
        double t = start;
        while (t < start + durationMs)
        {
            c.Push(Frame(t, ft, gpuBusy, cpuWait));
            t += ft;
        }
        return t;
    }

    [Fact]
    public void Detects_gpu_bound_then_switches_to_cpu_bound_after_hold()
    {
        var c = new BottleneckClassifier();
        double t = Feed(c, 0, 1500, ft: 10, gpuBusy: 9.6f);
        Assert.Equal(Bottleneck.Gpu, c.Current);
        Assert.InRange(c.GpuBusyRatio, 0.95, 0.97);

        // Lower resolution: GPU finishes early, CPU now limits.
        t = Feed(c, t, 1500, ft: 5, gpuBusy: 2f);
        Assert.Equal(Bottleneck.Gpu, c.Current); // still holding (window + hold not yet elapsed)

        Feed(c, t, 3000, ft: 5, gpuBusy: 2f);
        Assert.Equal(Bottleneck.Cpu, c.Current);
    }

    [Fact]
    public void Brief_blip_does_not_flip_label()
    {
        var c = new BottleneckClassifier();
        double t = Feed(c, 0, 2000, ft: 10, gpuBusy: 9.5f);
        t = Feed(c, t, 400, ft: 5, gpuBusy: 1f);
        Feed(c, t, 1500, ft: 10, gpuBusy: 9.5f);
        Assert.Equal(Bottleneck.Gpu, c.Current);
    }

    [Fact]
    public void Frame_limiter_reads_as_capped()
    {
        var c = new BottleneckClassifier();
        Feed(c, 0, 2000, ft: 16.6f, gpuBusy: 6f, cpuWait: 9f);
        Assert.Equal(Bottleneck.Capped, c.Current);
    }

    [Fact]
    public void Missing_gpu_busy_stays_unknown()
    {
        var c = new BottleneckClassifier();
        for (int i = 0; i < 500; i++)
            c.Push(new FrameSample(i * 10, 10, 10, 0, float.NaN, float.NaN, 10, float.NaN, float.NaN, float.NaN));
        Assert.Equal(Bottleneck.Unknown, c.Current);
    }
}
