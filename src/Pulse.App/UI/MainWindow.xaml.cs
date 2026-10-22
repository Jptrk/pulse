using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Pulse.App.UI;

/// <summary>Dashboard window. Closing it only hides it; Pulse keeps running in the tray.</summary>
internal partial class MainWindow : Window
{
    private readonly PulseHost _host;
    private readonly LiveView _live;
    private readonly SessionsView _sessions;
    private readonly SettingsView _settings;
    private readonly DispatcherTimer _timer;

    public MainWindow(PulseHost host)
    {
        _host = host;
        InitializeComponent();
        _live = new LiveView(host);
        _sessions = new SessionsView(host);
        _settings = new SettingsView(host);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                _timer.Start();
            else
                _timer.Stop();
        };
        Nav.SelectedIndex = 0;
        Refresh();
    }

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    public void Navigate(string tag)
    {
        foreach (ListBoxItem item in Nav.Items)
        {
            if ((string)item.Tag == tag)
                Nav.SelectedItem = item;
        }
    }

    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        Page.Content = (Nav.SelectedItem as ListBoxItem)?.Tag switch
        {
            "sessions" => _sessions,
            "settings" => _settings,
            _ => _live,
        };
        if (Page.Content == _sessions)
            _sessions.Reload();
        if (Page.Content == _settings)
            _settings.Load();
        Refresh();
    }

    private void Refresh()
    {
        var hub = _host.Hub;
        CaptureStatus.Text = "FPS capture: " + hub.CaptureStatus();
        AdminStatus.Text = hub.IsElevated ? "Running as admin" : "Not elevated — FPS & CPU temp off";
        DriverStatus.Text = hub.PawnIoInstalled ? "CPU sensor driver: PawnIO" : "CPU temp: driver not installed";
        HotkeyStatus.Text = _host.HotkeyProblem ?? $"{_host.Settings.Hotkeys.CycleMode} cycles overlay";
        if (WindowState != WindowState.Minimized && Page.Content == _live)
            _live.Refresh();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
