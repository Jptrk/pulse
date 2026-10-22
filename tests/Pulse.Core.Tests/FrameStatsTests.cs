using Pulse.Core.Stats;

namespace Pulse.Core.Tests;

public class FrameStatsTests
{
    [Fact]
    public void Constant_frame_times_give_matching_fps_and_lows()
    {
        float[] ft = Enumerable.Repeat(10f, 2000).ToArray();
        var r = FrameStats.Compute(ft);
        Assert.Equal(2000, r.Frames);
        Assert.Equal(100, r.AvgFps, 6);
        Assert.Equal(100, r.Low1Fps, 6);
        Assert.Equal(100, r.Low01Fps, 6);
        Assert.Equal(10, r.P99FrameTimeMs, 6);
    }

    [Fact]
    public void Average_fps_is_time_weighted()
    {
        // 1 frame of 100 ms + 9 frames of 10 ms = 10 frames in 190 ms => 52.6 fps (not the mean of per-frame fps, 91 fps).
        float[] ft = [100, 10, 10, 10, 10, 10, 10, 10, 10, 10];
        var r = FrameStats.Compute(ft);
        Assert.Equal(1000.0 * 10 / 190, r.AvgFps, 6);
    }

    [Fact]
    public void One_percent_low_is_mean_of_slowest_one_percent()
    {
        // 990 frames at 10 ms, 10 frames at 40 ms. Slowest 1% = the ten 40 ms frames => 25 fps.
        float[] ft = [.. Enumerable.Repeat(10f, 990), .. Enumerable.Repeat(40f, 10)];
        var r = FrameStats.Compute(ft);
        Assert.Equal(25, r.Low1Fps, 6);
        // Slowest 0.1% = 1 frame of 40 ms.
        Assert.Equal(25, r.Low01Fps, 6);
        Assert.Equal(40, r.MaxFrameTimeMs, 6);
    }

    [Fact]
    public void One_percent_low_rounds_slice_up()
    {
        // 150 frames => ceil(1.5) = 2 slowest frames: 30 and 20 ms => mean 25 => 40 fps.
        float[] ft = [.. Enumerable.Repeat(10f, 148), 20, 30];
        var r = FrameStats.Compute(ft);
        Assert.Equal(40, r.Low1Fps, 6);
    }

    [Fact]
    public void Lows_need_enough_frames()
    {
        var few = FrameStats.Compute(Enumerable.Repeat(10f, 99).ToArray());
        Assert.True(double.IsNaN(few.Low1Fps));
        Assert.True(double.IsNaN(few.Low01Fps));

        var some = FrameStats.Compute(Enumerable.Repeat(10f, 500).ToArray());
        Assert.False(double.IsNaN(some.Low1Fps));
        Assert.True(double.IsNaN(some.Low01Fps));
    }

    [Fact]
    public void Ignores_invalid_frame_times()
    {
        float[] ft = [10, float.NaN, 0, -5, float.PositiveInfinity, 10];
        var r = FrameStats.Compute(ft);
        Assert.Equal(2, r.Frames);
        Assert.Equal(100, r.AvgFps, 6);
    }

    [Fact]
    public void Empty_input_is_empty_result()
    {
        var r = FrameStats.Compute(ReadOnlySpan<float>.Empty);
        Assert.Equal(0, r.Frames);
        Assert.True(double.IsNaN(r.AvgFps));
    }

    [Theory]
    [InlineData(new float[] { 3, 1, 2 }, 2)]
    [InlineData(new float[] { 4, 1, 3, 2 }, 2.5)]
    [InlineData(new float[] { float.NaN, 5 }, 5)]
    public void Median(float[] values, double expected)
    {
        Assert.Equal(expected, FrameStats.Median(values, new float[values.Length]), 6);
    }
}

public class StutterDetectorTests
{
    [Fact]
    public void Flags_spike_after_warmup()
    {
        var d = new StutterDetector();
        for (int i = 0; i < 60; i++)
            Assert.False(d.Push(7f));
        Assert.Equal(7, d.MedianMs, 3);
        Assert.Equal(15, d.ThresholdMs, 3); // max(14, 7 + 8)
        Assert.False(d.Push(14f));
        Assert.True(d.Push(30f));
    }

    [Fact]
    public void Uses_ratio_at_low_fps()
    {
        var d = new StutterDetector();
        for (int i = 0; i < 60; i++)
            d.Push(33f);
        Assert.Equal(66, d.ThresholdMs, 3); // max(66, 41)
        Assert.False(d.Push(60f));
        Assert.True(d.Push(70f));
    }

    [Fact]
    public void No_stutters_during_warmup()
    {
        var d = new StutterDetector();
        d.Push(5f);
        Assert.False(d.Push(500f));
    }
}
