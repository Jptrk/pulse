using System.Diagnostics.Eventing.Reader;

namespace Pulse.App.Sensors;

/// <summary>
/// Counts Windows Hardware Error Architecture events as they're logged. Event 17 is a corrected PCIe error —
/// the usual symptom of a marginal riser cable or link speed. Reads the System event log only.
/// </summary>
internal sealed class WheaWatcher : IDisposable
{
    private const int MaxEvents = 10_000;

    private readonly Lock _lock = new();
    private readonly List<(DateTime Utc, int Id)> _events = [];
    private long _total;
    private EventLogWatcher? _watcher;

    public void Start()
    {
        try
        {
            var query = new EventLogQuery("System", PathType.LogName, "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger']]]");
            _watcher = new EventLogWatcher(query);
            _watcher.EventRecordWritten += OnEvent;
            _watcher.Enabled = true;
        }
        catch (Exception ex)
        {
            Log.Warn("WHEA watcher unavailable: " + ex.Message);
        }
    }

    private void OnEvent(object? sender, EventRecordWrittenEventArgs e)
    {
        if (e.EventRecord is null)
            return;
        int id;
        using (e.EventRecord)
            id = e.EventRecord.Id;
        long count;
        lock (_lock)
        {
            _events.Add((DateTime.UtcNow, id));
            if (_events.Count > MaxEvents)
                _events.RemoveRange(0, _events.Count - MaxEvents);
            count = ++_total;
        }
        // A flaky riser can log thousands of these; keep the log readable.
        if (count <= 10 || count % 100 == 0)
            Log.Warn($"WHEA event {id} logged ({count} since start)");
    }

    public int CountSince(DateTime utc)
    {
        lock (_lock)
            return _events.Count(x => x.Utc >= utc);
    }

    public void Dispose()
    {
        if (_watcher is not null)
        {
            _watcher.Enabled = false;
            _watcher.Dispose();
        }
    }
}
