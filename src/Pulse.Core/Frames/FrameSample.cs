namespace Pulse.Core.Frames;

/// <summary>
/// One presented frame as reported by PresentMon's v2 metrics.
/// All times are milliseconds. NaN means PresentMon reported "NA" or the column is absent.
/// </summary>
public readonly record struct FrameSample(
    double CpuStartMs,
    float FrameTimeMs,
    float CpuBusyMs,
    float CpuWaitMs,
    float GpuBusyMs,
    float GpuWaitMs,
    float DisplayedTimeMs,
    float DisplayLatencyMs,
    float PcLatencyMs,
    float ClickToPhotonMs)
{
    /// <summary>False when the frame was dropped and never reached the screen.</summary>
    public bool WasDisplayed => !float.IsNaN(DisplayedTimeMs);
}

/// <summary>A parsed PresentMon CSV row: which process presented, how, and the frame metrics.</summary>
public readonly record struct PresentMonRow(string Application, int ProcessId, string PresentMode, FrameSample Frame);
