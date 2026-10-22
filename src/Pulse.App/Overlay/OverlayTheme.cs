using System.Windows;
using System.Windows.Media;
using Pulse.Core.Analysis;

namespace Pulse.App.Overlay;

/// <summary>Frozen brushes, pens and typefaces for the overlay. Freezing lets WPF skip change tracking on every redraw.</summary>
internal static class OverlayTheme
{
    public static readonly Typeface Regular = Face(FontWeights.Normal);
    public static readonly Typeface SemiBold = Face(FontWeights.SemiBold);
    public static readonly Typeface Bold = Face(FontWeights.Bold);

    public const double LabelSize = 11;
    public const double ValueSize = 14.5;
    public const double UnitSize = 10.5;
    public const double BigSize = 30;
    public const double StripBigSize = 19;

    public static readonly Brush Label = Solid(0x8A, 0x94, 0xA6);
    public static readonly Brush Value = Solid(0xF3, 0xF6, 0xFA);
    public static readonly Brush Unit = Solid(0x6B, 0x75, 0x87);
    public static readonly Brush Dim = Solid(0x4B, 0x55, 0x63);

    public static readonly Brush Gpu = Solid(0x4A, 0xDE, 0x80);
    public static readonly Brush Cpu = Solid(0x60, 0xA5, 0xFA);
    public static readonly Brush Ram = Solid(0xC0, 0x84, 0xFC);
    public static readonly Brush Latency = Solid(0xF4, 0x72, 0xB6);
    public static readonly Brush Pcie = Solid(0x2D, 0xD4, 0xBF);
    public static readonly Brush Battery = Solid(0xA3, 0xE6, 0x35);
    public static readonly Brush Warn = Solid(0xFB, 0xBF, 0x24);
    public static readonly Brush Hot = Solid(0xF8, 0x71, 0x71);
    public static readonly Brush Neutral = Solid(0xA1, 0xA1, 0xAA);

    public static readonly Pen Border = FrozenPen(Solid(0xFF, 0xFF, 0xFF, 0x16), 1);
    public static readonly Pen GraphLine = FrozenPen(Solid(0xE5, 0xE7, 0xEB, 0xE0), 1.3);
    public static readonly Pen GraphGrid = FrozenPen(Solid(0xFF, 0xFF, 0xFF, 0x12), 1);
    public static readonly Pen StutterPen = FrozenPen(Hot, 1.6);
    public static readonly Brush GraphFill = FrozenGradient(Color.FromArgb(0x40, 0xE5, 0xE7, 0xEB), Color.FromArgb(0x00, 0xE5, 0xE7, 0xEB));
    public static readonly Brush BarTrack = Solid(0xFF, 0xFF, 0xFF, 0x14);

    public static Brush Background(double opacity) => Solid(0x0A, 0x0D, 0x12, (byte)Math.Clamp(opacity * 255, 0, 255));

    /// <summary>White when fine, amber within 10 °C of the alert threshold, red at or above it.</summary>
    public static Brush ForTemperature(double celsius, double hot) =>
        double.IsNaN(celsius) ? Value : celsius >= hot ? Hot : celsius >= hot - 10 ? Warn : Value;

    public static Brush ForLoad(double percent) => percent >= 90 ? Warn : Cpu;

    public static (string Text, Brush Brush)? Describe(Bottleneck b) => b switch
    {
        Bottleneck.Gpu => ("GPU-bound", Gpu),
        Bottleneck.Cpu => ("CPU-bound", Warn),
        Bottleneck.Capped => ("Capped · limiter/V-Sync", Cpu),
        Bottleneck.Balanced => ("Balanced", Neutral),
        _ => null,
    };

    private static Typeface Face(FontWeight weight)
    {
        var bahnschrift = new Typeface(new FontFamily("Bahnschrift"), FontStyles.Normal, weight, FontStretches.Normal);
        return bahnschrift.TryGetGlyphTypeface(out _) ? bahnschrift : new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal);
    }

    private static SolidColorBrush Solid(byte r, byte g, byte b, byte a = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }

    private static Brush FrozenGradient(Color top, Color bottom)
    {
        var brush = new LinearGradientBrush(top, bottom, 90);
        brush.Freeze();
        return brush;
    }
}
