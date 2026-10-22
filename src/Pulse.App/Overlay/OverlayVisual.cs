using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Pulse.Core.Settings;
using static Pulse.App.Overlay.OverlayTheme;

namespace Pulse.App.Overlay;

/// <summary>
/// Draws the overlay directly with DrawingVisuals — no WPF controls, bindings or layout passes per update.
/// Two layers: text (redrawn ~4 Hz) and the frame-time graph (redrawn ~30 Hz) so the graph never re-lays out text.
/// Value columns use fixed template widths ("000") so the overlay doesn't jitter as numbers change.
/// </summary>
internal sealed class OverlayVisual : FrameworkElement
{
    private const double PadX = 12;
    private const double LineGap = 3;
    private const double ColumnGap = 9;
    private const double LabelColumn = 44;
    private const double PanelMinWidth = 236;
    private const double GraphHeight = 46;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly DrawingVisual _textLayer = new();
    private readonly DrawingVisual _graphLayer = new();
    private readonly Dictionary<(string, Typeface, double), double> _widths = [];
    private OverlayMode _mode = OverlayMode.Panel;
    private Brush _background = Background(0.6);
    private int _graphCapacity = 240;
    private Size _size = new(1, 1);
    private Rect _graphRect = Rect.Empty;
    private double _graphMax = 16.7;
    private double _ppd = 1;

    // Line cursor, valid only while Update is drawing.
    private DrawingContext _dc = null!;
    private double _x, _y, _baseline, _lineHeight, _right, _padY;

    public OverlayVisual()
    {
        AddVisualChild(_textLayer);
        AddVisualChild(_graphLayer);
        IsHitTestVisible = false;
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
    }

    protected override int VisualChildrenCount => 2;

    protected override Visual GetVisualChild(int index) => index == 0 ? _textLayer : _graphLayer;

    protected override Size MeasureOverride(Size availableSize) => _size;

    protected override Size ArrangeOverride(Size finalSize) => _size;

    public OverlayMode Mode => _mode;

    /// <summary>Layout scale applied through LayoutTransform.</summary>
    public double Scale { get; private set; } = 1;

    /// <summary>Unscaled content size in DIPs after the last <see cref="Update"/>.</summary>
    public Size ContentSize => _size;

    /// <summary>Raised when an update changes the content size, so the host window can resize itself.</summary>
    public event Action? ContentSizeChanged;

    public void Configure(OverlayMode mode, double scale, double backgroundOpacity, int graphFrames)
    {
        _mode = mode;
        _background = Background(backgroundOpacity);
        _graphCapacity = Math.Clamp(graphFrames, 30, 2000);
        Scale = scale;
        LayoutTransform = Math.Abs(scale - 1) < 0.01 ? Transform.Identity : new ScaleTransform(scale, scale);
        _graphRect = Rect.Empty;
    }

    public void Update(OverlayModel model)
    {
        _ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var content = new DrawingGroup();
        using (DrawingContext dc = content.Open())
        {
            _dc = dc;
            _right = 0;
            _padY = _mode == OverlayMode.Strip ? 6 : 9;
            _y = _padY;
            if (_mode == OverlayMode.Strip)
                DrawStrip(model);
            else
                DrawPanel(model, detailed: _mode == OverlayMode.Detailed);
            _dc = null!;
        }

        var size = new Size(Math.Ceiling(_right + PadX), Math.Ceiling(_y - LineGap + _padY));
        using (DrawingContext dc = _textLayer.RenderOpen())
        {
            dc.DrawRoundedRectangle(_background, Border, new Rect(0.5, 0.5, size.Width - 1, size.Height - 1), 9, 9);
            dc.DrawDrawing(content);
        }

        if (size != _size)
        {
            _size = size;
            InvalidateMeasure();
            ContentSizeChanged?.Invoke();
        }
    }

    // ---------------------------------------------------------------- layouts

    private void DrawStrip(OverlayModel m)
    {
        var f = m.Frames;
        var hw = m.Hardware;

        BeginLine(StripBigSize);
        Text(F0(f?.CurrentFps), Bold, StripBigSize, Value, TemplateWidth("000", Bold, StripBigSize), alignRight: true);
        Gap(4);
        Label("FPS");
        Gap(10);
        Label("1%");
        Gap(4);
        Val(F0(f?.Window.Low1Fps), "000", LowBrush(f));

        Separator();
        Label("GPU", Gpu);
        Gap(5);
        Val(F0(hw?.GpuLoad), "100");
        Unit("%");
        Gap(7);
        Val(F0(hw?.GpuTempC), "00", ForTemperature(hw?.GpuTempC ?? double.NaN, m.GpuHotC));
        Unit("°");
        Gap(7);
        Val(F0(hw?.GpuPowerW), "000");
        Unit("W");

        Separator();
        Label("CPU", Cpu);
        Gap(5);
        Val(F0(hw?.CpuLoad), "100");
        Unit("%");
        Gap(7);
        Val(F0(hw?.CpuTempC), "00", ForTemperature(hw?.CpuTempC ?? double.NaN, m.CpuHotC));
        Unit("°");

        Separator();
        Label("RAM", Ram);
        Gap(5);
        Val(F1(hw?.RamUsedGb), "00.0");
        Unit("G");

        if (f is not null && Describe(f.Bottleneck) is { } b)
        {
            Separator();
            Dot(b.Brush);
            Text(b.Text.Split(' ')[0], SemiBold, LabelSize + 1, b.Brush);
        }

        foreach (var alert in m.Alerts)
        {
            Separator();
            Text("▲ " + alert.Message, SemiBold, LabelSize + 0.5, Hot);
        }
        EndLine();
        _graphRect = Rect.Empty;
    }

    private void DrawPanel(OverlayModel m, bool detailed)
    {
        var f = m.Frames;
        var hw = m.Hardware;
        _right = PadX + PanelMinWidth;

        // FPS headline
        BeginLine(BigSize);
        Text(F0(f?.CurrentFps), Bold, BigSize, Value, TemplateWidth("000", Bold, BigSize), alignRight: true);
        Gap(5);
        Label("FPS");
        _x = Math.Max(_x + ColumnGap, PadX + 112);
        Label("1%");
        Gap(4);
        Val(F0(f?.Window.Low1Fps), "000", LowBrush(f));
        Gap(10);
        Label("0.1%");
        Gap(4);
        Val(F0(f?.Window.Low01Fps), "000");
        EndLine();

        // Frame-time graph area (drawn by UpdateGraph)
        double graphTop = _y + 1;
        _y += GraphHeight + 7;

        if (detailed)
        {
            BeginLine();
            Label("LAT", Latency, LabelColumn);
            Val(F1(f?.DisplayLatencyMs), "00.0");
            Unit("ms display");
            if (f is not null && !double.IsNaN(f.PcLatencyMs))
            {
                Gap();
                Val(F1(f.PcLatencyMs), "00.0");
                Unit("ms PC");
            }
            else if (f is not null && !double.IsNaN(f.ClickToPhotonMs))
            {
                Gap();
                Val(F1(f.ClickToPhotonMs), "00.0");
                Unit("ms click");
            }
            EndLine();

            BeginLine();
            Label("FRAME", OverlayTheme.Label, LabelColumn);
            Val(F1(f?.Window.P99FrameTimeMs), "00.0");
            Unit("ms p99");
            Gap();
            Val(f is null ? "--" : f.StuttersTotal.ToString(Inv), "000", f is { StuttersTotal: > 0 } ? Warn : Value);
            Unit("stutters");
            EndLine();
        }

        // GPU
        BeginLine();
        Label("GPU", Gpu, LabelColumn);
        Val(F0(hw?.GpuLoad), "100");
        Unit("%");
        Gap();
        Val(F0(hw?.GpuTempC), "00", ForTemperature(hw?.GpuTempC ?? double.NaN, m.GpuHotC));
        Unit("°C");
        Gap();
        Val(F0(hw?.GpuPowerW), "000");
        Unit("W");
        EndLine();

        BeginLine();
        _x += LabelColumn;
        Val(F0(hw?.GpuClockMhz), "0000");
        Unit("MHz");
        Gap();
        Label("VRAM");
        Gap(4);
        Val(F1(hw?.VramUsedMb / 1024), "00.0");
        Unit(hw is { VramTotalMb: > 0 } ? $"/ {hw.VramTotalMb / 1024:0} GB" : "GB");
        EndLine();

        if (detailed)
        {
            BeginLine();
            _x += LabelColumn;
            Label("FAN");
            Gap(4);
            Val(F0(hw?.GpuFanRpm), "0000");
            Unit("rpm");
            Gap();
            Label("MEM");
            Gap(4);
            Val(F0(hw?.GpuMemJunctionC), "00", ForTemperature(hw?.GpuMemJunctionC ?? double.NaN, 100));
            Unit("°C");
            EndLine();
        }

        // CPU
        BeginLine();
        Label("CPU", Cpu, LabelColumn);
        Val(F0(hw?.CpuLoad), "100");
        Unit("%");
        Gap();
        Val(F0(hw?.CpuTempC), "00", ForTemperature(hw?.CpuTempC ?? double.NaN, m.CpuHotC));
        Unit("°C");
        Gap();
        Val(F0(hw?.CpuPowerW), "000");
        Unit("W");
        EndLine();

        BeginLine();
        _x += LabelColumn;
        Val(hw is null || double.IsNaN(hw.CpuClockMhz) ? "--" : (hw.CpuClockMhz / 1000).ToString("0.00", Inv), "0.00");
        Unit("GHz");
        Gap();
        Label("TOP THREAD");
        Gap(4);
        Val(F0(hw?.CpuMaxThreadLoad), "100", hw is { CpuMaxThreadLoad: >= 95 } ? Warn : Value);
        Unit("%");
        EndLine();

        if (detailed && hw is { ThreadLoads.Length: > 0 })
            ThreadBars(hw.ThreadLoads);

        // RAM
        BeginLine();
        Label("RAM", Ram, LabelColumn);
        Val(F1(hw?.RamUsedGb), "00.0");
        Unit(hw is { RamTotalGb: > 0 } ? $"/ {hw.RamTotalGb:0} GB" : "GB");
        EndLine();

        if (detailed && m.Pcie is { } pcie)
        {
            BeginLine();
            Label("PCIe", OverlayTheme.Pcie, LabelColumn);
            Text($"Gen{pcie.Gen} x{pcie.Width}", SemiBold, ValueSize, Value);
            Gap();
            Val(m.PcieReplaysRecent.ToString(Inv), "000", m.PcieReplaysRecent > 0 ? Hot : Value);
            Unit("retries/min");
            EndLine();
        }

        if (f is not null && Describe(f.Bottleneck) is { } b)
        {
            BeginLine();
            Dot(b.Brush);
            Text(b.Text, SemiBold, LabelSize + 1, b.Brush);
            if (!double.IsNaN(f.GpuBusyRatio))
            {
                Gap();
                Label($"GPU busy {f.GpuBusyRatio * 100:0}%");
            }
            EndLine();
        }

        foreach (var alert in m.Alerts)
        {
            BeginLine(LabelSize + 1);
            Text("▲ " + alert.Message, SemiBold, LabelSize + 0.5, Hot);
            EndLine();
        }

        if (detailed && m.GameName is { } game)
        {
            BeginLine(LabelSize);
            Text($"{game} · {(int)m.GameTime.TotalMinutes}:{m.GameTime.Seconds:00}", Regular, LabelSize, Dim);
            EndLine();
        }

        _graphRect = new Rect(PadX, graphTop, _right - PadX, GraphHeight);
    }

    private void ThreadBars(float[] loads)
    {
        const double barWidth = 7, barGap = 2.5, height = 18;
        double x = PadX + LabelColumn;
        double bottom = _y + height;
        foreach (float load in loads)
        {
            var track = new Rect(x, _y, barWidth, height);
            _dc.DrawRoundedRectangle(BarTrack, null, track, 1.5, 1.5);
            double h = Math.Clamp(load / 100.0, 0, 1) * height;
            if (h > 0.5)
                _dc.DrawRoundedRectangle(ForLoad(load), null, new Rect(x, bottom - h, barWidth, h), 1.5, 1.5);
            x += barWidth + barGap;
        }
        _right = Math.Max(_right, x);
        _y += height + LineGap + 2;
    }

    // ---------------------------------------------------------------- graph

    /// <param name="frameTimes">Newest frames, oldest first.</param>
    public void UpdateGraph(ReadOnlySpan<float> frameTimes, double stutterThresholdMs)
    {
        using DrawingContext dc = _graphLayer.RenderOpen();
        if (_graphRect.IsEmpty)
            return;

        Rect r = _graphRect;
        dc.DrawLine(GraphGrid, new Point(r.Left, r.Top + r.Height / 2), new Point(r.Right, r.Top + r.Height / 2));
        dc.DrawLine(GraphGrid, new Point(r.Left, r.Bottom), new Point(r.Right, r.Bottom));

        int n = frameTimes.Length;
        if (n < 2)
        {
            dc.DrawText(Format("waiting for frames…", Regular, 10, Dim), new Point(r.Left + 2, r.Top + r.Height / 2 - 14));
            return;
        }

        // Scale: rise instantly, decay slowly. Huge hitches clip at the top instead of flattening the whole graph.
        float max = 0;
        foreach (float v in frameTimes)
            max = Math.Max(max, v);
        double cap = double.IsNaN(stutterThresholdMs) ? 50 : Math.Max(stutterThresholdMs * 2.5, 33.4);
        double target = Math.Max(4, Math.Min(max * 1.1, cap));
        _graphMax = target > _graphMax ? target : _graphMax * 0.97 + target * 0.03;

        double step = r.Width / (_graphCapacity - 1);
        double x0 = r.Right - (n - 1) * step;
        double Y(float v) => r.Bottom - Math.Min(v / _graphMax, 1) * r.Height;

        var fill = new StreamGeometry();
        var line = new StreamGeometry();
        using (StreamGeometryContext f = fill.Open())
        using (StreamGeometryContext l = line.Open())
        {
            f.BeginFigure(new Point(x0, r.Bottom), isFilled: true, isClosed: true);
            l.BeginFigure(new Point(x0, Y(frameTimes[0])), isFilled: false, isClosed: false);
            for (int i = 0; i < n; i++)
            {
                var p = new Point(x0 + i * step, Y(frameTimes[i]));
                f.LineTo(p, false, false);
                if (i > 0)
                    l.LineTo(p, true, true);
            }
            f.LineTo(new Point(x0 + (n - 1) * step, r.Bottom), false, false);
        }
        fill.Freeze();
        line.Freeze();
        dc.DrawGeometry(GraphFill, null, fill);
        dc.DrawGeometry(null, GraphLine, line);

        if (!double.IsNaN(stutterThresholdMs))
        {
            for (int i = 0; i < n; i++)
            {
                if (frameTimes[i] > stutterThresholdMs)
                {
                    double x = x0 + i * step;
                    dc.DrawLine(StutterPen, new Point(x, r.Bottom), new Point(x, Y(frameTimes[i])));
                }
            }
        }

        double recent = 0;
        int k = Math.Min(n, 15);
        for (int i = n - k; i < n; i++)
            recent += frameTimes[i];
        dc.DrawText(Format($"{_graphMax:0} ms", Regular, 9.5, Dim), new Point(r.Left + 2, r.Top));
        var now = Format($"{recent / k:0.0} ms", SemiBold, 10.5, OverlayTheme.Label);
        dc.DrawText(now, new Point(r.Right - now.Width - 2, r.Top));
    }

    public void ClearGraph()
    {
        using (_graphLayer.RenderOpen()) { }
    }

    // ---------------------------------------------------------------- text primitives

    private void BeginLine(double largestFont = ValueSize)
    {
        FormattedText probe = Format("0", SemiBold, largestFont, Value);
        _x = PadX;
        _baseline = probe.Baseline;
        _lineHeight = probe.Height;
    }

    private void EndLine()
    {
        _right = Math.Max(_right, _x);
        _y += _lineHeight + LineGap;
    }

    private void Text(string text, Typeface face, double size, Brush brush, double minWidth = 0, bool alignRight = false)
    {
        FormattedText ft = Format(text, face, size, brush);
        double w = ft.WidthIncludingTrailingWhitespace;
        double slot = Math.Max(w, minWidth);
        double x = alignRight ? _x + slot - w : _x;
        _dc.DrawText(ft, new Point(x, _y + _baseline - ft.Baseline));
        _x += slot;
    }

    private void Label(string text, Brush? brush = null, double minWidth = 0) =>
        Text(text, SemiBold, LabelSize, brush ?? OverlayTheme.Label, minWidth);

    private void Val(string text, string template, Brush? brush = null) =>
        Text(text, SemiBold, ValueSize, brush ?? Value, TemplateWidth(template, SemiBold, ValueSize), alignRight: true);

    private void Unit(string text)
    {
        _x += 2;
        Text(text, Regular, UnitSize, OverlayTheme.Unit);
    }

    private void Gap(double width = ColumnGap) => _x += width;

    private void Separator()
    {
        Gap(9);
        _dc.DrawRectangle(Dim, null, new Rect(_x, _y + 3, 1, Math.Max(4, _lineHeight - 6)));
        Gap(10);
    }

    private void Dot(Brush brush)
    {
        double cy = _y + _baseline - ValueSize * 0.32;
        _dc.DrawEllipse(brush, null, new Point(_x + 4, cy), 3.5, 3.5);
        _x += 13;
    }

    private FormattedText Format(string text, Typeface face, double size, Brush brush) =>
        new(text, Inv, FlowDirection.LeftToRight, face, size, brush, _ppd);

    private double TemplateWidth(string template, Typeface face, double size)
    {
        if (!_widths.TryGetValue((template, face, size), out double w))
        {
            w = Format(template, face, size, Value).WidthIncludingTrailingWhitespace;
            _widths[(template, face, size)] = w;
        }
        return w;
    }

    private static Brush LowBrush(Pulse.Core.Frames.FrameWindowStats? f) =>
        f is not null && f.Window.Low1Fps < f.Window.AvgFps * 0.5 ? Warn : Value;

    private static string F0(double? v) => v is double d && double.IsFinite(d) ? Math.Round(d).ToString("0", Inv) : "--";

    private static string F1(double? v) => v is double d && double.IsFinite(d) ? d.ToString("0.0", Inv) : "--";
}
