namespace Pulse.App.Sensors;

/// <summary>One poll of all hardware sensors. NaN = sensor not available (e.g. CPU temp without the PawnIO driver).</summary>
internal sealed record HardwareReading
{
    public string CpuName { get; init; } = "";
    public double CpuLoad { get; init; } = double.NaN;
    public double CpuMaxThreadLoad { get; init; } = double.NaN;
    public float[] ThreadLoads { get; init; } = [];
    public double CpuClockMhz { get; init; } = double.NaN;
    public double CpuTempC { get; init; } = double.NaN;
    public double CpuPowerW { get; init; } = double.NaN;

    public string GpuName { get; init; } = "";
    public double GpuLoad { get; init; } = double.NaN;
    public double GpuTempC { get; init; } = double.NaN;
    public double GpuHotspotC { get; init; } = double.NaN;
    public double GpuMemJunctionC { get; init; } = double.NaN;
    public double GpuClockMhz { get; init; } = double.NaN;
    public double GpuMemClockMhz { get; init; } = double.NaN;
    public double GpuPowerW { get; init; } = double.NaN;
    public double GpuFanRpm { get; init; } = double.NaN;
    public double GpuFanPercent { get; init; } = double.NaN;
    public double VramUsedMb { get; init; } = double.NaN;
    public double VramTotalMb { get; init; } = double.NaN;

    public double RamUsedGb { get; init; } = double.NaN;
    public double RamTotalGb { get; init; } = double.NaN;

    public double VramPercent => VramTotalMb > 0 ? 100 * VramUsedMb / VramTotalMb : double.NaN;
    public double RamPercent => RamTotalGb > 0 ? 100 * RamUsedGb / RamTotalGb : double.NaN;
}

/// <summary>GPU PCIe link state from NVML. Replay counter counts link-level retransmits since boot.</summary>
internal sealed record PcieReading(int Gen, int Width, int MaxGen, int MaxWidth, long ReplayCounter);
