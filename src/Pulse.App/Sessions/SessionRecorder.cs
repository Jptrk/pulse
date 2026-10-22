using System.Collections.Concurrent;
using Pulse.App.Storage;
using Pulse.Core.Analysis;
using Pulse.Core.Frames;
using Pulse.Core.Sessions;

namespace Pulse.App.Sessions;

/// <summary>
/// Records one game session at a time. Frames arrive on the capture thread, samples on the UI thread;
/// both go into a locked accumulator. Database writes — and the end-of-session summary, which sorts every
/// frame — run in order on one background thread, batched every ~10 s, so neither the game nor the UI waits.
/// </summary>
internal sealed class SessionRecorder : IDisposable
{
    private const double MinSessionSeconds = 30;
    private const int MinFrames = 300;

    /// <summary>Database id of one session, filled in on the writer thread once the row exists.</summary>
    private sealed class SessionRow
    {
        public long Id = -1;
    }

    private readonly SessionStore _store;
    private readonly FramePipeline _pipeline;
    private readonly Lock _lock = new();
    private readonly BlockingCollection<Action> _writes = new();
    private readonly Thread _writer;
    private readonly List<SensorSample> _pendingSamples = [];
    private SessionAccumulator? _session;
    private SessionRow? _row;
    private int _chunk;
    private double _startMs;

    public SessionRecorder(SessionStore store, FramePipeline pipeline)
    {
        _store = store;
        _pipeline = pipeline;
        _pipeline.ActiveFrame += OnFrame;
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Session writer" };
        _writer.Start();
    }

    /// <summary>Raised on the writer thread after a session is saved (or discarded as too short).</summary>
    public event Action? SessionsChanged;

    public bool IsRecording
    {
        get { lock (_lock) return _session is not null; }
    }

    public double ElapsedSeconds(double nowMs) => (nowMs - _startMs) / 1000;

    public void Start(DetectedGame game, double nowMs)
    {
        End();
        var row = new SessionRow();
        string exe = game.ExeName;
        DateTime started = DateTime.UtcNow;
        lock (_lock)
        {
            _session = new SessionAccumulator(exe, started);
            _row = row;
            _pendingSamples.Clear();
            _chunk = 0;
            _startMs = nowMs;
        }
        _writes.Add(() => row.Id = _store.CreateSession(exe, started));
        Log.Info($"Session started: {exe} (pid {game.ProcessId})");
    }

    private void OnFrame(FrameSample frame, bool stutter, Bottleneck bottleneck)
    {
        lock (_lock)
            _session?.AddFrame(frame.FrameTimeMs, stutter, bottleneck);
    }

    public void AddSample(SensorSample sample)
    {
        lock (_lock)
        {
            if (_session is null)
                return;
            _session.AddSample(sample);
            _pendingSamples.Add(sample);
        }
    }

    public void SetCounters(long pcieReplays, long wheaEvents)
    {
        lock (_lock)
        {
            if (_session is null)
                return;
            _session.PcieReplays = pcieReplays;
            _session.WheaEvents = wheaEvents;
        }
    }

    /// <summary>Queues everything collected since the last flush.</summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (_session is not null)
                QueueAppendLocked();
        }
    }

    private void QueueAppendLocked()
    {
        var samples = _pendingSamples.ToArray();
        _pendingSamples.Clear();
        float[] frames = _session!.TakeUnflushedFrames();
        int chunk = _chunk++;
        SessionRow row = _row!;
        if (samples.Length > 0 || frames.Length > 0)
        {
            _writes.Add(() =>
            {
                if (row.Id >= 0)
                    _store.Append(row.Id, samples, chunk, frames);
            });
        }
    }

    public void End()
    {
        SessionAccumulator session;
        SessionRow row;
        lock (_lock)
        {
            if (_session is null)
                return;
            QueueAppendLocked();
            session = _session;
            row = _row!;
            _session = null; // frames stop flowing into it from here on, so the writer can read it unlocked
            _row = null;
        }

        DateTime ended = DateTime.UtcNow;
        _writes.Add(() =>
        {
            if (row.Id < 0)
                return;
            SessionSummary summary = session.Summarize(ended);
            if (summary.DurationSeconds < MinSessionSeconds || summary.Frames < MinFrames)
            {
                _store.Delete(row.Id);
                Log.Info($"Session discarded (too short): {summary.Exe}");
            }
            else
            {
                _store.Finish(row.Id, summary);
                Log.Info($"Session saved: {summary.Exe}, {summary.DurationSeconds:0}s, avg {summary.AvgFps:0.0} fps, 1% {summary.Low1Fps:0.0}");
            }
            SessionsChanged?.Invoke();
        });
    }

    private void WriteLoop()
    {
        foreach (Action write in _writes.GetConsumingEnumerable())
        {
            try
            {
                write();
            }
            catch (Exception ex)
            {
                Log.Error("Session write failed", ex);
            }
        }
    }

    public void Dispose()
    {
        _pipeline.ActiveFrame -= OnFrame;
        End();
        _writes.CompleteAdding();
        _writer.Join(10_000);
    }
}
