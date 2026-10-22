using ScottPlot;
using ScottPlot.WPF;

namespace Pulse.App.UI;

/// <summary>Shared dark styling for ScottPlot charts so they sit flush inside the cards.</summary>
internal static class ChartStyle
{
    public static readonly Color Gpu = Color.FromHex("#4ADE80");
    public static readonly Color Cpu = Color.FromHex("#60A5FA");
    public static readonly Color Ram = Color.FromHex("#C084FC");
    public static readonly Color Fps = Color.FromHex("#F3F6FA");
    public static readonly Color Low = Color.FromHex("#FBBF24");
    public static readonly Color Hot = Color.FromHex("#F87171");

    public static void Apply(WpfPlot control, string? yLabel = null)
    {
        Plot plot = control.Plot;
        plot.FigureBackground.Color = Color.FromHex("#161A21");
        plot.DataBackground.Color = Color.FromHex("#161A21");
        plot.Axes.Color(Color.FromHex("#5B6474"));
        plot.Grid.MajorLineColor = Color.FromHex("#232833");
        plot.Legend.BackgroundColor = Color.FromHex("#1C212A");
        plot.Legend.FontColor = Color.FromHex("#C9D1DC");
        plot.Legend.OutlineColor = Color.FromHex("#232833");
        plot.Legend.Alignment = Alignment.UpperLeft;
        plot.Axes.Left.Label.Text = yLabel ?? "";
        plot.Axes.Left.Label.FontSize = 12;
        plot.Axes.Bottom.TickLabelStyle.FontSize = 11;
        plot.Axes.Left.TickLabelStyle.FontSize = 11;
        control.UserInputProcessor.Disable();
    }
}
