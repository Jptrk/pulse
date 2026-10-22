using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Pulse.App.Native;
using Pulse.Core.Frames;

namespace Pulse.App.Capture;

/// <summary>
/// Runs the bundled PresentMon console app and streams its CSV output into <see cref="FramePipeline"/>.
/// PresentMon reads Windows' own ETW frame events. It does open each presenting process with
/// PROCESS_QUERY_LIMITED_INFORMATION (name lookup and exit detection) — the weakest access right, the same
/// one Task Manager uses; it cannot read memory or inject. Pulse itself never opens the game.
/// The child lives in a kill-on-close job object so it can't outlive Pulse, and is restarted with backoff if it exits.
/// </summary>
internal sealed class PresentMonHost : IDisposable
{
    public const string SessionName = "PulseETW";

    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    private readonly string _exePath;
    private readonly FramePipeline _pipeline;
    private readonly ManualResetEventSlim _stop = new();
    private readonly nint _job;
    private Thread? _thread;
    private Process? _process;

    public PresentMonHost(string exePath, FramePipeline pipeline)
    {
        _exePath = exePath;
        _pipeline = pipeline;
        _job = CreateKillOnCloseJob();
    }

    public string Status { get; private set; } = "Starting";

    public bool IsRunning => Status == "Running";

    public void Start()
    {
        _thread = new Thread(Supervise) { IsBackground = true, Name = "PresentMon reader", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Supervise()
    {
        int failures = 0;
        while (!_stop.IsSet)
        {
            var started = DateTime.UtcNow;
            try
            {
                RunOnce();
            }
            catch (Exception ex)
            {
                Log.Error("PresentMon capture failed", ex);
                Status = "Error: " + ex.Message;
            }

            if (_stop.IsSet)
                break;

            // A run that lasted a while was healthy; start the backoff over.
            failures = DateTime.UtcNow - started > TimeSpan.FromMinutes(1) ? 0 : failures + 1;
            TimeSpan wait = Backoff[Math.Min(failures, Backoff.Length - 1)];
            if (!Status.StartsWith("Error", StringComparison.Ordinal))
                Status = $"Restarting in {wait.TotalSeconds:0}s";
            _stop.Wait(wait);
        }
    }

    private void RunOnce()
    {
        if (!File.Exists(_exePath))
        {
            Status = "Error: PresentMon.exe missing";
            _stop.Wait(Timeout.Infinite);
            return;
        }

        var psi = new ProcessStartInfo(_exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in new[]
                 {
                     "--output_stdout", "--v2_metrics", "--track_pc_latency", "--no_console_stats",
                     "--stop_existing_session", "--session_name", SessionName,
                     "--exclude", "dwm.exe", "--exclude", Path.GetFileName(Environment.ProcessPath ?? "Pulse.exe"),
                 })
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("PresentMon did not start.");
        _process = process;
        if (_job != 0)
            Win32.AssignProcessToJobObject(_job, process.Handle);

        string? lastError = null;
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                lastError = e.Data.Trim();
        };
        process.BeginErrorReadLine();

        PresentMonCsvParser? parser = null;
        StreamReader stdout = process.StandardOutput;
        string? line;
        while ((line = stdout.ReadLine()) is not null)
        {
            if (parser is null)
            {
                if (PresentMonCsvParser.LooksLikeHeader(line))
                {
                    parser = new PresentMonCsvParser(line);
                    _pipeline.ResetTimeline(); // a new run's timestamps start from zero again
                    Status = "Running";
                    Log.Info($"PresentMon streaming; GPU busy: {parser.HasGpuBusy}, PC latency: {parser.HasPcLatency}");
                }
                continue;
            }

            if (parser.TryParse(line, out PresentMonRow row))
                _pipeline.Ingest(row, AppClock.NowMs);
        }

        process.WaitForExit(2000);
        _process = null;
        if (!_stop.IsSet)
        {
            string reason = lastError ?? $"exit code {(process.HasExited ? process.ExitCode : -1)}";
            Log.Warn("PresentMon exited: " + reason);
            Status = reason.Contains("access", StringComparison.OrdinalIgnoreCase) || reason.Contains("elevat", StringComparison.OrdinalIgnoreCase)
                ? "Error: needs admin"
                : "Stopped: " + reason;
        }
    }

    private static nint CreateKillOnCloseJob()
    {
        nint job = Win32.CreateJobObjectW(0, null);
        if (job == 0)
            return 0;
        var info = new Win32.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = { LimitFlags = Win32.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
        };
        if (!Win32.SetInformationJobObject(job, Win32.JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf(info)))
        {
            Win32.CloseHandle(job);
            return 0;
        }
        return job;
    }

    public void Dispose()
    {
        _stop.Set();
        try
        {
            if (_process is { HasExited: false } p)
                p.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.Warn("Stopping PresentMon: " + ex.Message);
        }
        _thread?.Join(3000);
        if (_job != 0)
            Win32.CloseHandle(_job);
        StopEtwSession();
    }

    /// <summary>Killing PresentMon leaves its real-time ETW session running; ask PresentMon to stop it.</summary>
    private void StopEtwSession()
    {
        if (!File.Exists(_exePath))
            return;
        try
        {
            var psi = new ProcessStartInfo(_exePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--terminate_existing_session");
            psi.ArgumentList.Add("--session_name");
            psi.ArgumentList.Add(SessionName);
            using var p = Process.Start(psi);
            if (p is not null && !p.WaitForExit(3000))
                p.Kill();
        }
        catch (Exception ex)
        {
            Log.Warn("Stopping ETW session: " + ex.Message);
        }
    }
}
