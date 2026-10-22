using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Pulse.App.Telemetry;
using Pulse.Core.Analysis;
using ScottPlot.WPF;
using SColor = ScottPlot.Color;

namespace Pulse.App.UI;

internal partial class LiveView : UserControl
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly PulseHost _host;
    private readonly List<Rectangle> _bars = [];
    private readonly double[] _xs;

    public LiveView(PulseHost host)
    {
        _host = host;
        InitializeComponent();
        _xs = Enumerable.Range(0, host.Hub.History.Length).Select(i => (double)(i - host.Hub.History.Length + 1)).ToArray();
        ChartStyle.Apply(FpsChart);
        ChartStyle.Apply(TempChart);
        ChartStyle.Apply(LoadChart);
        ChartStyle.Apply(PowerChart);
    }

    public void Refresh()
    {
        var hub = _host.Hub;
        var hw = hub.Hardware;
        var game = hub.Game;
        var frames = game is null ? null : hub.Frames;

        GameDot.Fill = (Brush)FindResource(game is null ? "DimTextBrush" : "GpuBrush");
        GameText.Text = game is null
            ? "No game detected"
            : $"{System.IO.Path.GetFileNameWithoutExtension(game.ExeName)} · {FormatDuration(TimeSpan.FromMilliseconds(AppClock.NowMs - game.StartedMs))}";
        OverlayText.Text = _host.Overlay.Hidden ? "Overlay hidden" : $"Overlay: {_host.Overlay.Mode}";

        FpsValue.Text = F0(frames?.CurrentFps);
        FpsLows.Text = frames is null ? "1% low --  ·  0.1% low --" : $"1% low {F0(frames.Window.Low1Fps)}  ·  0.1% low {F0(frames.Window.Low01Fps)}";
        FpsFrameTime.Text = frames is null ? "Frame time --" : $"Frame time {F1(frames.CurrentFrameTimeMs)} ms  ·  p99 {F1(frames.Window.P99FrameTimeMs)} ms";
        FpsLatency.Text = frames is null ? "" : LatencyText(frames.DisplayLatencyMs, frames.PcLatencyMs) + $"  ·  {frames.StuttersTotal} stutters";
        var b = frames is null ? null : Overlay.OverlayTheme.Describe(frames.Bottleneck);
        FpsBottleneck.Text = b is null ? (game is null ? "Start a game to see FPS" : "Analysing…") : b.Value.Text + (double.IsNaN(frames!.GpuBusyRatio) ? "" : $"  ·  GPU busy {frames.GpuBusyRatio * 100:0}%");
        FpsBottleneck.Foreground = b?.Brush ?? (Brush)FindResource("MutedTextBrush");

        GpuTitle.Text = string.IsNullOrEmpty(hw?.GpuName) ? "GPU" : hw.GpuName.ToUpperInvariant();
        GpuLoad.Text = F0(hw?.GpuLoad);
        GpuTemp.Text = F0(hw?.GpuTempC);
        GpuTemp.Foreground = Overlay.OverlayTheme.ForTemperature(hw?.GpuTempC ?? double.NaN, _host.Settings.Alerts.GpuTempC);
        GpuLine1.Text = $"{F0(hw?.GpuPowerW)} W  ·  {F0(hw?.GpuClockMhz)} MHz";
        GpuLine2.Text = hw is { VramTotalMb: > 0 } ? $"VRAM {hw.VramUsedMb / 1024:0.0} / {hw.VramTotalMb / 1024:0} GB  ({hw.VramPercent:0}%)" : "VRAM --";
        GpuLine3.Text = $"Fan {F0(hw?.GpuFanRpm)} rpm  ·  memory {F0(hw?.GpuMemJunctionC)} °C";

        CpuTitle.Text = string.IsNullOrEmpty(hw?.CpuName) ? "CPU" : hw.CpuName.ToUpperInvariant();
        CpuLoad.Text = F0(hw?.CpuLoad);
        CpuTemp.Text = F0(hw?.CpuTempC);
        CpuTemp.Foreground = Overlay.OverlayTheme.ForTemperature(hw?.CpuTempC ?? double.NaN, _host.Settings.Alerts.CpuTempC);
        CpuLine1.Text = $"{(hw is null || double.IsNaN(hw.CpuClockMhz) ? "--" : (hw.CpuClockMhz / 1000).ToString("0.00", Inv))} GHz  ·  {F0(hw?.CpuPowerW)} W";
        CpuLine2.Text = $"Busiest thread {F0(hw?.CpuMaxThreadLoad)}%";
        UpdateBars(hw?.ThreadLoads ?? []);

        RamUsed.Text = F1(hw?.RamUsedGb);
        RamTotal.Text = hw is { RamTotalGb: > 0 } ? $"/ {hw.RamTotalGb:0} GB" : "GB";
        RamLine.Text = hw is { RamTotalGb: > 0 } ? $"{hw.RamPercent:0}% in use" : "";

        var pcie = hub.Pcie;
        PcieLine.Text = pcie is null ? "Not available" : $"Gen{pcie.Gen} x{pcie.Width}  (max Gen{pcie.MaxGen} x{pcie.MaxWidth})";
        PcieHealth.Text = pcie is null ? "" : pcie.ReplayCounter < 0 ? "Retry counter unavailable" : $"{pcie.ReplayCounter} link retries since boot";
        PcieHealth.Foreground = (Brush)FindResource(pcie is { ReplayCounter: > 0 } ? "WarnBrush" : "MutedTextBrush");

        var h = hub.History;
        Draw(FpsChart, 0, null, (h.Fps, ChartStyle.Fps, "FPS"), (h.Low1, ChartStyle.Low, "1% low"));
        Draw(TempChart, 20, 100, (h.GpuTemp, ChartStyle.Gpu, "GPU"), (h.CpuTemp, ChartStyle.Cpu, "CPU"));
        Draw(LoadChart, 0, 100, (h.GpuLoad, ChartStyle.Gpu, "GPU"), (h.CpuLoad, ChartStyle.Cpu, "CPU"));
        Draw(PowerChart, 0, null, (h.GpuPower, ChartStyle.Gpu, "GPU"), (h.CpuPower, ChartStyle.Cpu, "CPU"));
    }

    /// <summary>Redraws a chart from finite points only, so missing sensors leave gaps instead of breaking the axes.</summary>
    private void Draw(WpfPlot chart, double yMin, double? yMax, params (double[] Series, SColor Color, string Name)[] series)
    {
        var plot = chart.Plot;
        plot.Clear();
        double max = yMin + 10;
        foreach (var (data, color, name) in series)
        {
            var xs = new List<double>(data.Length);
            var ys = new List<double>(data.Length);
            for (int i = 0; i < data.Length; i++)
            {
                if (double.IsFinite(data[i]))
                {
                    xs.Add(_xs[i]);
                    ys.Add(data[i]);
                    max = Math.Max(max, data[i]);
                }
            }
            if (xs.Count < 2)
                continue;
            var line = plot.Add.Scatter(xs.ToArray(), ys.ToArray());
            line.Color = color;
            line.LineWidth = 2;
            line.MarkerSize = 0;
            line.LegendText = name;
        }
        plot.Axes.SetLimits(_xs[0], 0, yMin, yMax ?? Math.Ceiling(max * 1.15 / 10) * 10);
        plot.ShowLegend();
        chart.Refresh();
    }

    private void UpdateBars(float[] loads)
    {
        if (_bars.Count != loads.Length)
        {
            _bars.Clear();
            ThreadBars.Items.Clear();
            for (int i = 0; i < loads.Length; i++)
            {
                var rect = new Rectangle { Width = 9, RadiusX = 2, RadiusY = 2, VerticalAlignment = VerticalAlignment.Bottom };
                var track = new Grid { Width = 9, Height = 22, Margin = new Thickness(0, 0, 3, 0), Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)) };
                track.Children.Add(rect);
                ThreadBars.Items.Add(track);
                _bars.Add(rect);
            }
        }
        for (int i = 0; i < loads.Length; i++)
        {
            _bars[i].Height = Math.Clamp(loads[i] / 100.0, 0.02, 1) * 22;
            _bars[i].Fill = (Brush)FindResource(loads[i] >= 90 ? "WarnBrush" : "CpuBrush");
        }
    }

    private static string LatencyText(double display, double pc) =>
        !double.IsNaN(pc) ? $"PC latency {pc:0.0} ms" : !double.IsNaN(display) ? $"Display latency {display:0.0} ms" : "Latency --";

    private static string FormatDuration(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", Inv) : t.ToString(@"m\:ss", Inv);

    private static string F0(double? v) => v is double d && double.IsFinite(d) ? Math.Round(d).ToString("0", Inv) : "--";

    private static string F1(double? v) => v is double d && double.IsFinite(d) ? d.ToString("0.0", Inv) : "--";
}
