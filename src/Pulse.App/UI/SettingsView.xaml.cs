using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Pulse.App.Native;
using Pulse.App.Startup;
using Pulse.Core.Settings;

namespace Pulse.App.UI;

internal partial class SettingsView : UserControl
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly PulseHost _host;

    public SettingsView(PulseHost host)
    {
        _host = host;
        InitializeComponent();
        Mode.ItemsSource = Enum.GetValues<OverlayMode>();
        Corner.ItemsSource = Enum.GetValues<OverlayCorner>();
        ScaleSlider.ValueChanged += (_, _) => ScaleText.Text = $"{ScaleSlider.Value * 100:0}%";
        OpacitySlider.ValueChanged += (_, _) => OpacityText.Text = $"{OpacitySlider.Value * 100:0}%";
    }

    /// <summary>Copies current settings into the form.</summary>
    public void Load()
    {
        var s = _host.Settings;
        Mode.SelectedItem = s.Overlay.Mode;
        Corner.SelectedItem = s.Overlay.Corner;
        OffsetX.Text = s.Overlay.OffsetX.ToString(Inv);
        OffsetY.Text = s.Overlay.OffsetY.ToString(Inv);
        ScaleSlider.Value = s.Overlay.Scale;
        OpacitySlider.Value = s.Overlay.BackgroundOpacity;
        StatsWindow.Text = s.Overlay.StatsWindowSeconds.ToString(Inv);
        OnlyInGame.IsChecked = s.Overlay.OnlyWhenGameDetected;

        CycleKey.Text = s.Hotkeys.CycleMode;
        HideKey.Text = s.Hotkeys.ToggleVisible;

        AlertsOn.IsChecked = s.Alerts.Enabled;
        CpuHot.Text = s.Alerts.CpuTempC.ToString(Inv);
        GpuHot.Text = s.Alerts.GpuTempC.ToString(Inv);
        VramFull.Text = s.Alerts.VramPercent.ToString(Inv);
        RamFull.Text = s.Alerts.RamPercent.ToString(Inv);
        PcieOn.IsChecked = s.Alerts.PcieChecks;
        PcieGen.Text = s.Alerts.ExpectedPcieGen.ToString(Inv);
        PcieWidth.Text = s.Alerts.ExpectedPcieWidth.ToString(Inv);
        WheaOn.IsChecked = s.Alerts.WheaChecks;

        AllowList.Text = string.Join(Environment.NewLine, s.Games.Allow);
        DenyList.Text = string.Join(Environment.NewLine, s.Games.Deny);

        Autostart.IsChecked = AutostartTask.IsEnabled();
        Autostart.IsEnabled = _host.Hub.IsElevated;
        PollMs.Text = s.Sensors.PollIntervalMs.ToString(Inv);

        DriverText.Text = _host.Hub.PawnIoInstalled
            ? "PawnIO is installed, so CPU temperature and power are available. If an anti-cheat ever blocks it, uninstall PawnIO from Apps & features — Pulse keeps working without it."
            : "Not installed. Everything except CPU temperature and CPU power works without it. CPU temperature needs the signed PawnIO driver (winget install namazso.PawnIO). Test your anti-cheat games first — see README.";
        SaveStatus.Text = "";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var s = _host.Settings;
        var errors = new List<string>();

        if (!HotkeyService.TryParse(CycleKey.Text, out _, out _))
            errors.Add($"Cycle hotkey \"{CycleKey.Text}\" isn't valid.");
        if (!string.IsNullOrWhiteSpace(HideKey.Text) && !HotkeyService.TryParse(HideKey.Text, out _, out _))
            errors.Add($"Hide hotkey \"{HideKey.Text}\" isn't valid.");
        if (string.Equals(CycleKey.Text.Replace(" ", ""), HideKey.Text.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
            errors.Add("The two hotkeys must be different.");
        if (errors.Count > 0)
        {
            SaveStatus.Text = string.Join(" ", errors);
            return;
        }

        s.Overlay.Mode = (OverlayMode)(Mode.SelectedItem ?? s.Overlay.Mode);
        s.Overlay.Corner = (OverlayCorner)(Corner.SelectedItem ?? s.Overlay.Corner);
        s.Overlay.OffsetX = Int(OffsetX.Text, s.Overlay.OffsetX, 0, 4000);
        s.Overlay.OffsetY = Int(OffsetY.Text, s.Overlay.OffsetY, 0, 4000);
        s.Overlay.Scale = Math.Round(ScaleSlider.Value, 2);
        s.Overlay.BackgroundOpacity = Math.Round(OpacitySlider.Value, 2);
        s.Overlay.StatsWindowSeconds = Int(StatsWindow.Text, s.Overlay.StatsWindowSeconds, 5, 300);
        s.Overlay.OnlyWhenGameDetected = OnlyInGame.IsChecked == true;

        s.Hotkeys.CycleMode = CycleKey.Text.Trim();
        s.Hotkeys.ToggleVisible = HideKey.Text.Trim();

        s.Alerts.Enabled = AlertsOn.IsChecked == true;
        s.Alerts.CpuTempC = Num(CpuHot.Text, s.Alerts.CpuTempC, 50, 110);
        s.Alerts.GpuTempC = Num(GpuHot.Text, s.Alerts.GpuTempC, 50, 110);
        s.Alerts.VramPercent = Num(VramFull.Text, s.Alerts.VramPercent, 50, 100);
        s.Alerts.RamPercent = Num(RamFull.Text, s.Alerts.RamPercent, 50, 100);
        s.Alerts.PcieChecks = PcieOn.IsChecked == true;
        s.Alerts.ExpectedPcieGen = Int(PcieGen.Text, s.Alerts.ExpectedPcieGen, 1, 6);
        s.Alerts.ExpectedPcieWidth = Int(PcieWidth.Text, s.Alerts.ExpectedPcieWidth, 1, 16);
        s.Alerts.WheaChecks = WheaOn.IsChecked == true;

        s.Games.Allow = Lines(AllowList.Text);
        s.Games.Deny = Lines(DenyList.Text);
        s.Sensors.PollIntervalMs = Int(PollMs.Text, s.Sensors.PollIntervalMs, 250, 5000);

        string status = "Saved.";
        bool wantAutostart = Autostart.IsChecked == true;
        if (_host.Hub.IsElevated && wantAutostart != AutostartTask.IsEnabled())
        {
            bool ok = wantAutostart ? AutostartTask.Enable(out string err) : AutostartTask.Disable(out err);
            s.Autostart = wantAutostart && ok;
            if (!ok)
            {
                status = "Saved, but changing autostart failed: " + err;
                Log.Warn("Autostart change failed: " + err);
            }
        }

        _host.ApplySettings();
        Load();
        SaveStatus.Text = _host.HotkeyProblem is { } p ? $"Saved. {p}" : status;
    }

    private static int Int(string text, int fallback, int min, int max) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, Inv, out int v) ? Math.Clamp(v, min, max) : fallback;

    private static double Num(string text, double fallback, double min, double max) =>
        double.TryParse(text.Trim(), NumberStyles.Float, Inv, out double v) ? Math.Clamp(v, min, max) : fallback;

    private static List<string> Lines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
