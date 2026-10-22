using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pulse.App.Sensors;
using Pulse.Core.Analysis;
using Pulse.Core.Frames;
using Pulse.Core.Settings;
using Pulse.Core.Stats;

namespace Pulse.App.Overlay;

/// <summary>Renders each overlay mode with sample data to PNGs (pulse.exe --preview DIR). Used to check the design without a game.</summary>
internal static class OverlayPreview
{
    public static void Render(string directory)
    {
        Directory.CreateDirectory(directory);
        var rng = new Random(7);
        float[] frames = Enumerable.Range(0, 240)
            .Select(i => (float)(6.9 + rng.NextDouble() * 1.2 + (i is 150 or 151 ? 24 : 0) + (i == 205 ? 16 : 0)))
            .ToArray();

        var stats = new FrameWindowStats(
            ProcessId: 1,
            Window: new FrameStatsResult(4300, 143.6, 118.2, 96.7, 6.96, 9.8, 31.2),
            CurrentFps: 144.2, CurrentFrameTimeMs: 6.93, Bottleneck: Bottleneck.Gpu, GpuBusyRatio: 0.96,
            StutterThresholdMs: 15.2, StuttersTotal: 3, DisplayLatencyMs: 12.4, PcLatencyMs: 18.6, ClickToPhotonMs: double.NaN,
            PresentMode: "Hardware: Independent Flip");

        var hw = new HardwareReading
        {
            CpuName = "AMD Ryzen 7 9700X", CpuLoad = 41, CpuMaxThreadLoad = 92, CpuClockMhz = 5420, CpuTempC = 78, CpuPowerW = 88,
            ThreadLoads = [72, 40, 92, 35, 66, 20, 58, 31, 44, 12, 61, 25, 38, 9, 49, 18],
            GpuName = "NVIDIA GeForce RTX 5070 Ti", GpuLoad = 97, GpuTempC = 64, GpuClockMhz = 2805, GpuMemClockMhz = 14001,
            GpuPowerW = 287, GpuFanRpm = 1450, GpuFanPercent = 45, GpuMemJunctionC = 70, VramUsedMb = 9318, VramTotalMb = 16303,
            RamUsedGb = 14.2, RamTotalGb = 31.1,
        };

        var model = new OverlayModel(stats, hw, new PcieReading(4, 16, 5, 16, 0), 0, "VALORANT-Win64-Shipping",
            TimeSpan.FromMinutes(12.6), [], CpuHotC: 90, GpuHotC: 83);
        var alerting = model with
        {
            Hardware = hw with { CpuTempC = 91 },
            Alerts = [new ActiveAlert("cpu-hot", "CPU 91°C — running hot", 3), new ActiveAlert("pcie-gen", "PCIe Gen5 — expected Gen4", 1)],
        };

        foreach (var (name, mode, m) in new[]
                 {
                     ("strip", OverlayMode.Strip, model),
                     ("panel", OverlayMode.Panel, model),
                     ("detailed", OverlayMode.Detailed, model),
                     ("panel-alerts", OverlayMode.Panel, alerting),
                     ("strip-alerts", OverlayMode.Strip, alerting),
                 })
        {
            var visual = new OverlayVisual();
            visual.Configure(mode, 1.0, 0.7, 240);
            visual.Update(m);
            visual.UpdateGraph(frames, stats.StutterThresholdMs);
            visual.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            visual.Arrange(new Rect(visual.DesiredSize));
            Save(visual, Path.Combine(directory, $"overlay-{name}.png"));
        }
    }

    /// <summary>Composites the overlay on a game-like backdrop at 2x so transparency and legibility can be judged.</summary>
    private static void Save(OverlayVisual visual, string path)
    {
        const double scale = 2;
        Size size = visual.DesiredSize;
        double w = size.Width + 48, h = size.Height + 48;

        var host = new DrawingVisual();
        using (DrawingContext dc = host.RenderOpen())
        {
            var sky = new LinearGradientBrush(Color.FromRgb(0x5B, 0x8F, 0xC7), Color.FromRgb(0xD9, 0xA0, 0x6B), 90);
            dc.DrawRectangle(sky, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2E, 0x3B, 0x2A)), null, new Rect(0, h * 0.62, w, h * 0.38));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xF1, 0xC4)), null, new Point(w * 0.8, h * 0.25), 30, 30);
            dc.PushTransform(new TranslateTransform(24, 24));
            dc.DrawRectangle(new VisualBrush(visual), null, new Rect(size));
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap((int)(w * scale), (int)(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
