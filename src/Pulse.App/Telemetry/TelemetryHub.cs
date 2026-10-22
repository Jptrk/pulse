using Pulse.App.Sensors;
using Pulse.Core.Analysis;
using Pulse.Core.Frames;

namespace Pulse.App.Telemetry;

/// <summary>
/// Latest values from every producer. Each field is an immutable object swapped atomically by its producer,
/// so readers (overlay, tray, main window) never lock and never see half-updated data.
/// </summary>
internal sealed class TelemetryHub
{
    private volatile HardwareReading? _hardware;
    private volatile PcieReading? _pcie;
    private volatile FrameWindowStats? _frames;
    private volatile DetectedGame? _game;
    private volatile IReadOnlyList<ActiveAlert> _alerts = [];

    public HardwareReading? Hardware { get => _hardware; set => _hardware = value; }
    public PcieReading? Pcie { get => _pcie; set => _pcie = value; }
    public FrameWindowStats? Frames { get => _frames; set => _frames = value; }
    public DetectedGame? Game { get => _game; set => _game = value; }
    public IReadOnlyList<ActiveAlert> Alerts { get => _alerts; set => _alerts = value; }

    /// <summary>Foreground window and its process, refreshed twice a second by the host.</summary>
    public nint ForegroundHwnd { get; set; }
    public int ForegroundPid { get; set; }

    public bool IsElevated { get; init; }
    public bool PawnIoInstalled { get; init; }
    public Func<string> CaptureStatus { get; set; } = () => "Off";

    /// <summary>Last five minutes at 1 Hz for the dashboard charts. Touched only on the UI thread.</summary>
    public History History { get; } = new(300);
}

/// <summary>Fixed-length rolling series; index 0 is oldest. Series arrays are stable so charts can hold references.</summary>
internal sealed class History(int length)
{
    public int Length { get; } = length;
    public double[] CpuLoad { get; } = Filled(length);
    public double[] GpuLoad { get; } = Filled(length);
    public double[] CpuTemp { get; } = Filled(length);
    public double[] GpuTemp { get; } = Filled(length);
    public double[] CpuPower { get; } = Filled(length);
    public double[] GpuPower { get; } = Filled(length);
    public double[] Fps { get; } = Filled(length);
    public double[] Low1 { get; } = Filled(length);

    public void Push(HardwareReading? hw, FrameWindowStats? frames)
    {
        Shift(CpuLoad, hw?.CpuLoad);
        Shift(GpuLoad, hw?.GpuLoad);
        Shift(CpuTemp, hw?.CpuTempC);
        Shift(GpuTemp, hw?.GpuTempC);
        Shift(CpuPower, hw?.CpuPowerW);
        Shift(GpuPower, hw?.GpuPowerW);
        Shift(Fps, frames?.CurrentFps);
        Shift(Low1, frames?.Window.Low1Fps);
    }

    private static void Shift(double[] series, double? value)
    {
        Array.Copy(series, 1, series, 0, series.Length - 1);
        series[^1] = value ?? double.NaN;
    }

    private static double[] Filled(int n)
    {
        var a = new double[n];
        Array.Fill(a, double.NaN);
        return a;
    }
}
