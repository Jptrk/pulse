using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Pulse.App.Native;
using Pulse.App.Overlay;
using Pulse.App.Sensors;
using Pulse.App.Startup;
using Pulse.App.UI;

namespace Pulse.App;

/// <summary>
/// Entry point. Command line:
///   --minimized        start in the tray (used by the autostart task)
///   --no-elevate       don't ask for admin (FPS capture and CPU temperature stay off)
///   --preview DIR      render overlay mock-ups to PNG and exit
///   --snapshot DIR     render each dashboard page to PNG after a few seconds of live data, then exit
///   --dump-sensors F   write every hardware sensor to a text file and exit
///   --exit-after S     quit after S seconds (smoke tests)
///   --quit             ask a running Pulse to exit and wait for it (installer/uninstaller)
///   --register-autostart / --unregister-autostart   create or remove the logon task, then exit (installer)
/// </summary>
public partial class App : Application
{
    private SingleInstance? _instance;
    private PulseHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("Unhandled UI exception", ex.Exception);
            ex.Handled = true;
            if (_host is null)
                Shutdown(); // failed during startup: don't linger as an invisible process
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("Unhandled exception", ex.ExceptionObject as Exception);

        string[] args = e.Args;
        if (Arg(args, "--preview") is { } previewDir)
        {
            OverlayPreview.Render(previewDir);
            Shutdown();
            return;
        }
        if (Arg(args, "--dump-sensors") is { } dumpFile)
        {
            using (var writer = new StreamWriter(dumpFile))
                SensorService.Dump(writer);
            Shutdown();
            return;
        }

        if (args.Contains("--quit"))
        {
            if (SingleInstance.SignalQuit() && !SingleInstance.WaitUntilGone(TimeSpan.FromSeconds(15)))
                Log.Warn("--quit: running instance did not exit within 15 s");
            Shutdown();
            return;
        }
        if (args.Contains("--register-autostart") || args.Contains("--unregister-autostart"))
        {
            bool register = args.Contains("--register-autostart");
            if (!(register ? AutostartTask.Enable(out string error) : AutostartTask.Disable(out error)))
                Log.Warn($"Autostart {(register ? "registration" : "removal")} failed: {error}");
            Shutdown();
            return;
        }

        // Check for a running copy before asking for elevation, so a second launch never shows a UAC prompt.
        if (SingleInstance.IsRunning())
        {
            SingleInstance.SignalShow();
            Shutdown();
            return;
        }

        string? snapshotDir = Arg(args, "--snapshot");
        bool noElevate = args.Contains("--no-elevate") || snapshotDir is not null;
        if (!Elevation.IsElevated && !noElevate)
        {
            if (Elevation.TryRelaunchElevated(args))
            {
                Shutdown();
                return;
            }
            Log.Warn("Elevation declined; running with limited features.");
        }

        _instance = new SingleInstance();
        if (!_instance.TryAcquire())
        {
            SingleInstance.SignalShow();
            _instance.Dispose();
            _instance = null;
            Shutdown();
            return;
        }

        _host = new PulseHost();
        _host.Start();
        ListenForShowRequests();

        if (snapshotDir is not null)
            Snapshot(snapshotDir);
        else if (!args.Contains("--minimized"))
            _host.ShowMainWindow();

        if (double.TryParse(Arg(args, "--exit-after"), System.Globalization.CultureInfo.InvariantCulture, out double seconds))
        {
            var exitTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            exitTimer.Tick += (_, _) => Shutdown();
            exitTimer.Start();
        }
    }

    /// <summary>
    /// A second launch signals "show" so the running instance opens its window instead;
    /// the installer signals "quit" so files can be replaced or removed after a clean shutdown.
    /// </summary>
    private void ListenForShowRequests()
    {
        if (_instance is not { ShowRequested: { } show, QuitRequested: { } quit })
            return;
        var thread = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    if (WaitHandle.WaitAny([show, quit]) == 1)
                    {
                        Log.Info("Quit requested by another process.");
                        Dispatcher.BeginInvoke(Shutdown);
                        return;
                    }
                    Dispatcher.BeginInvoke(() => _host?.ShowMainWindow());
                }
            }
            catch (ObjectDisposedException)
            {
                // Shutting down.
            }
        }) { IsBackground = true, Name = "Show listener" };
        thread.Start();
    }

    private void Snapshot(string directory)
    {
        Directory.CreateDirectory(directory);
        _host!.ShowMainWindow();
        var window = Windows.OfType<MainWindow>().First();
        string[] pages = ["live", "sessions", "settings"];
        int index = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) =>
        {
            if (index > 0)
                Save(window, Path.Combine(directory, $"ui-{pages[index - 1]}.png"));
            if (index == pages.Length)
            {
                timer.Stop();
                Shutdown();
                return;
            }
            window.Navigate(pages[index++]);
            timer.Interval = TimeSpan.FromSeconds(1.5);
        };
        timer.Start();
    }

    private static void Save(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        double dpi = VisualTreeHelper.GetDpi(window).PixelsPerInchX;
        double scale = dpi / 96;
        var bitmap = new RenderTargetBitmap((int)(content.ActualWidth * scale), (int)(content.ActualHeight * scale), dpi, dpi, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (DrawingContext dc = background.RenderOpen())
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        bitmap.Render(background);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
