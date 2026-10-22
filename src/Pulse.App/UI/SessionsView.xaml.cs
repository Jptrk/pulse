using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Pulse.App.Storage;
using Pulse.Core.Sessions;
using ScottPlot.WPF;
using SColor = ScottPlot.Color;

namespace Pulse.App.UI;

internal partial class SessionsView : UserControl
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly SColor[] CompareColors = [ChartStyle.Gpu, ChartStyle.Cpu];

    private const int MaxChartPoints = 4000;

    private readonly PulseHost _host;
    private int _loadVersion;

    /// <summary>One row of the session table, formatted for display.</summary>
    internal sealed record Row(StoredSession Session)
    {
        private SessionSummary S => Session.Summary;
        public string Game => Path.GetFileNameWithoutExtension(S.Exe) + (Session.Complete ? "" : "  (recording)");
        public string When => S.StartedUtc.ToLocalTime().ToString("ddd d MMM  HH:mm", Inv);
        public string Length => Duration(S.DurationSeconds);
        public string Avg => F1(S.AvgFps);
        public string Low1 => F1(S.Low1Fps);
        public string Low01 => F1(S.Low01Fps);
        public string Stutters => S.Stutters.ToString(Inv);
        public string CpuMax => Temp(S.MaxCpuTempC);
        public string GpuMax => Temp(S.MaxGpuTempC);
        public string GpuBound => double.IsFinite(S.PctGpuBound) ? $"{S.PctGpuBound:0}%" : "--";
        public string Pcie => double.IsFinite(S.MinPcieGen) ? $"Gen{S.MinPcieGen:0} x{S.MinPcieWidth:0}" : "--";
    }

    public SessionsView(PulseHost host)
    {
        _host = host;
        InitializeComponent();
        ChartStyle.Apply(TopChart);
        ChartStyle.Apply(BottomChart);
        host.SessionsChanged += () => Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible)
                Reload();
        });
    }

    public void Reload()
    {
        try
        {
            SessionGrid.ItemsSource = _host.Store.List().Select(s => new Row(s)).ToList();
        }
        catch (Exception ex)
        {
            Log.Error("Loading sessions failed", ex);
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Reload();

    private List<Row> Selected => SessionGrid.SelectedItems.Cast<Row>().ToList();

    private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var rows = Selected;
        ExportButton.IsEnabled = rows.Count == 1;
        // The live session is still being written; deleting it would leave the recorder appending to a missing row.
        DeleteButton.IsEnabled = rows.Count > 0 && rows.All(r => r.Session.Complete);
        SelectionHint.Text = rows.Count == 2 ? "Comparing 2 sessions" : "Select two sessions to compare";
        int version = ++_loadVersion;

        try
        {
            if (rows.Count == 1)
                await ShowSession(rows[0].Session, version);
            else if (rows.Count == 2)
                await ShowComparison(rows[1].Session, rows[0].Session, version);
            else
                Clear();
        }
        catch (Exception ex)
        {
            Log.Error("Showing session failed", ex);
        }
    }

    private async Task ShowSession(StoredSession session, int version)
    {
        var s = session.Summary;
        DetailTitle.Text = $"{Path.GetFileNameWithoutExtension(s.Exe).ToUpperInvariant()}  ·  {s.StartedUtc.ToLocalTime():f}";
        DetailText.Text = Describe(s);

        // Hours of frames are millions of points: load and reduce them off the UI thread.
        var (frameXs, frameYs, yMax, samples) = await Task.Run(() =>
        {
            float[] frames = _host.Store.LoadFrames(session.Id);
            var (xs, ys) = DownsampleFrameTimes(frames, MaxChartPoints);
            float[] sorted = (float[])frames.Clone();
            Array.Sort(sorted);
            double p99 = sorted.Length > 0 ? sorted[(int)(sorted.Length * 0.99)] : 10;
            return (xs, ys, Math.Max(10, p99 * 2.5), _host.Store.LoadSamples(session.Id));
        });
        if (version != _loadVersion)
            return; // selection changed while loading

        TopChartTitle.Text = "FRAME TIMES · ms";
        var top = TopChart.Plot;
        top.Clear();
        if (frameXs.Length > 1)
        {
            var line = top.Add.Scatter(frameXs, frameYs);
            line.Color = ChartStyle.Fps;
            line.LineWidth = 1;
            line.MarkerSize = 0;
            line.LegendText = "Frame time";
            top.Axes.SetLimits(0, frameXs[^1], 0, yMax);
        }
        TopChart.Refresh();

        BottomChartTitle.Text = "TEMPERATURE · °C";
        var bottom = BottomChart.Plot;
        bottom.Clear();
        AddSeries(bottom, samples, s => s.GpuTempC, ChartStyle.Gpu, "GPU");
        AddSeries(bottom, samples, s => s.CpuTempC, ChartStyle.Cpu, "CPU");
        bottom.Axes.AutoScale();
        bottom.ShowLegend();
        BottomChart.Refresh();
    }

    private async Task ShowComparison(StoredSession a, StoredSession b, int version)
    {
        DetailTitle.Text = "COMPARISON";
        var sa = a.Summary;
        var sb = b.Summary;
        var text = new StringBuilder();
        text.AppendLine($"A  {Label(sa)}");
        text.AppendLine($"B  {Label(sb)}");
        text.AppendLine();
        Compare(text, "Avg FPS", sa.AvgFps, sb.AvgFps, higherIsBetter: true);
        Compare(text, "1% low", sa.Low1Fps, sb.Low1Fps, higherIsBetter: true);
        Compare(text, "0.1% low", sa.Low01Fps, sb.Low01Fps, higherIsBetter: true);
        Compare(text, "p99 frame ms", sa.P99FrameTimeMs, sb.P99FrameTimeMs, higherIsBetter: false);
        Compare(text, "Stutters/min", PerMinute(sa.Stutters, sa.DurationSeconds), PerMinute(sb.Stutters, sb.DurationSeconds), higherIsBetter: false);
        Compare(text, "CPU max °C", sa.MaxCpuTempC, sb.MaxCpuTempC, higherIsBetter: false);
        Compare(text, "GPU max °C", sa.MaxGpuTempC, sb.MaxGpuTempC, higherIsBetter: false);
        Compare(text, "GPU max W", sa.MaxGpuPowerW, sb.MaxGpuPowerW, higherIsBetter: false);
        DetailText.Text = text.ToString();

        TopChartTitle.Text = "FPS OVER TIME · A vs B";
        BottomChartTitle.Text = "GPU TEMPERATURE · A vs B";
        var top = TopChart.Plot;
        var bottom = BottomChart.Plot;
        top.Clear();
        bottom.Clear();
        var loaded = await Task.Run(() => new[] { _host.Store.LoadSamples(a.Id), _host.Store.LoadSamples(b.Id) });
        if (version != _loadVersion)
            return;
        for (int i = 0; i < loaded.Length; i++)
        {
            string name = i == 0 ? "A" : "B";
            AddSeries(top, loaded[i], s => s.Fps, CompareColors[i], name);
            AddSeries(bottom, loaded[i], s => s.GpuTempC, CompareColors[i], name);
        }
        top.Axes.AutoScale();
        bottom.Axes.AutoScale();
        top.ShowLegend();
        bottom.ShowLegend();
        TopChart.Refresh();
        BottomChart.Refresh();
    }

    private void Clear()
    {
        DetailTitle.Text = "Select a session";
        DetailText.Text = "";
        TopChart.Plot.Clear();
        BottomChart.Plot.Clear();
        TopChart.Refresh();
        BottomChart.Refresh();
    }

    /// <summary>
    /// Frame times against elapsed seconds, reduced to about <paramref name="maxPoints"/> by keeping each bucket's
    /// min and max in time order — so every stutter spike still shows up.
    /// </summary>
    private static (double[] Xs, double[] Ys) DownsampleFrameTimes(float[] frames, int maxPoints)
    {
        if (frames.Length < 2)
            return ([], []);
        int perBucket = Math.Max(1, (int)Math.Ceiling(frames.Length / (maxPoints / 2.0)));
        var xs = new List<double>(maxPoints + 2);
        var ys = new List<double>(maxPoints + 2);
        double t = 0;
        for (int start = 0; start < frames.Length; start += perBucket)
        {
            int end = Math.Min(frames.Length, start + perBucket);
            int minAt = start, maxAt = start;
            double bucketStart = t;
            for (int i = start; i < end; i++)
            {
                if (frames[i] < frames[minAt])
                    minAt = i;
                if (frames[i] > frames[maxAt])
                    maxAt = i;
                t += frames[i] / 1000.0;
            }
            if (perBucket == 1)
            {
                xs.Add(t);
                ys.Add(frames[start]);
                continue;
            }
            double mid = (bucketStart + t) / 2;
            xs.Add(mid);
            ys.Add(frames[Math.Min(minAt, maxAt)]);
            if (minAt != maxAt)
            {
                xs.Add(mid);
                ys.Add(frames[Math.Max(minAt, maxAt)]);
            }
        }
        return (xs.ToArray(), ys.ToArray());
    }

    private static void AddSeries(ScottPlot.Plot plot, List<SensorSample> samples, Func<SensorSample, double> value, SColor color, string name)
    {
        var points = samples.Where(s => double.IsFinite(value(s))).ToList();
        if (points.Count < 2)
            return;
        var line = plot.Add.Scatter(points.Select(p => p.ElapsedSeconds / 60).ToArray(), points.Select(value).ToArray());
        line.Color = color;
        line.LineWidth = 2;
        line.MarkerSize = 0;
        line.LegendText = name;
        plot.Axes.Bottom.Label.Text = "minutes";
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (Selected is not [var row])
            return;
        var dialog = new SaveFileDialog
        {
            FileName = $"{Path.GetFileNameWithoutExtension(row.Session.Summary.Exe)}-{row.Session.Summary.StartedUtc.ToLocalTime():yyyyMMdd-HHmm}.csv",
            Filter = "CSV|*.csv",
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            await Task.Run(() => _host.Store.ExportCsv(row.Session.Id, dialog.FileName));
            DetailTitle.Text = "Exported sensors + frame times next to " + Path.GetFileName(dialog.FileName);
        }
        catch (Exception ex)
        {
            Log.Error("Export failed", ex);
            MessageBox.Show(ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        var rows = Selected;
        if (rows.Count == 0)
            return;
        string what = rows.Count == 1 ? Path.GetFileNameWithoutExtension(rows[0].Session.Summary.Exe) + " session" : $"{rows.Count} sessions";
        if (MessageBox.Show($"Delete {what}? This can't be undone.", "Delete sessions", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        foreach (var row in rows)
            _host.Store.Delete(row.Session.Id);
        Reload();
    }

    private static string Describe(SessionSummary s) => string.Join('\n',
        $"Length          {Duration(s.DurationSeconds)}",
        $"Frames          {s.Frames:N0}",
        $"Avg FPS         {F1(s.AvgFps)}",
        $"1% low          {F1(s.Low1Fps)}",
        $"0.1% low        {F1(s.Low01Fps)}",
        $"p99 frame       {F1(s.P99FrameTimeMs)} ms",
        $"Stutters        {s.Stutters}",
        $"GPU-bound       {Pct(s.PctGpuBound)}",
        $"CPU-bound       {Pct(s.PctCpuBound)}",
        "",
        $"CPU max         {Temp(s.MaxCpuTempC)}   avg load {Pct(s.AvgCpuLoad)}",
        $"GPU max         {Temp(s.MaxGpuTempC)}   avg load {Pct(s.AvgGpuLoad)}",
        $"GPU peak power  {F0(s.MaxGpuPowerW)} W",
        $"VRAM peak       {(double.IsFinite(s.MaxVramMb) ? (s.MaxVramMb / 1024).ToString("0.0", Inv) : "--")} GB",
        "",
        $"PCIe under load {(double.IsFinite(s.MinPcieGen) ? $"Gen{s.MinPcieGen:0} x{s.MinPcieWidth:0}" : "--")}",
        $"Link retries    {s.PcieReplays}",
        $"WHEA errors     {s.WheaEvents}");

    private static void Compare(StringBuilder text, string name, double a, double b, bool higherIsBetter)
    {
        string delta = "";
        if (double.IsFinite(a) && double.IsFinite(b) && Math.Abs(a) > 1e-9)
        {
            double pct = (b - a) / a * 100;
            bool better = higherIsBetter ? pct > 0 : pct < 0;
            delta = Math.Abs(pct) < 0.5 ? "  =" : $"  {(pct > 0 ? "+" : "")}{pct:0}% {(better ? "▲" : "▼")}";
        }
        text.AppendLine($"{name,-14}{F1(a),7} → {F1(b),-7}{delta}");
    }

    private static string Label(SessionSummary s) => $"{Path.GetFileNameWithoutExtension(s.Exe)} · {s.StartedUtc.ToLocalTime():d MMM HH:mm}";

    private static double PerMinute(long count, double seconds) => seconds > 0 ? count / (seconds / 60) : double.NaN;

    private static string Duration(double seconds)
    {
        if (!double.IsFinite(seconds))
            return "--";
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{t.Minutes}m {t.Seconds:00}s";
    }

    private static string Temp(double v) => double.IsFinite(v) ? $"{v:0} °C" : "--";

    private static string Pct(double v) => double.IsFinite(v) ? $"{v:0}%" : "--";

    private static string F0(double v) => double.IsFinite(v) ? v.ToString("0", Inv) : "--";

    private static string F1(double v) => double.IsFinite(v) ? v.ToString("0.0", Inv) : "--";
}
