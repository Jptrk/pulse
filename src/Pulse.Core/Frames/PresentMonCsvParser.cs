using System.Globalization;

namespace Pulse.Core.Frames;

/// <summary>
/// Parses PresentMon console CSV rows. Column positions come from the header line, so
/// reordered or added columns are fine; optional metrics that are missing parse as NaN.
/// </summary>
public sealed class PresentMonCsvParser
{
    private const int MaxPooledStrings = 512;

    private readonly int _columnCount;
    private readonly int _app, _pid, _mode, _cpuStart, _frameTime;
    private readonly int _cpuBusy, _cpuWait, _gpuBusy, _gpuWait;
    private readonly int _displayed, _displayLatency, _pcLatency, _clickToPhoton;

    // Application and PresentMode repeat on every row; pool them so steady-state parsing doesn't allocate strings.
    private readonly Dictionary<string, string> _pool = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _poolLookup;

    public PresentMonCsvParser(string headerLine)
    {
        string[] columns = headerLine.Trim().Split(',');
        _columnCount = columns.Length;

        int Find(params string[] names)
        {
            foreach (string name in names)
            {
                int index = Array.FindIndex(columns, c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                    return index;
            }
            return -1;
        }

        _app = Find("Application");
        _pid = Find("ProcessID");
        _mode = Find("PresentMode");
        _cpuStart = Find("CPUStartTime", "CPUStartTimeInMs");
        _frameTime = Find("FrameTime", "MsBetweenPresents");
        _cpuBusy = Find("CPUBusy", "MsCPUBusy");
        _cpuWait = Find("CPUWait", "MsCPUWait");
        _gpuBusy = Find("GPUBusy", "MsGPUBusy");
        _gpuWait = Find("GPUWait", "MsGPUWait");
        _displayed = Find("DisplayedTime", "MsBetweenDisplayChange");
        _displayLatency = Find("DisplayLatency", "MsUntilDisplayed");
        _pcLatency = Find("PCLatency", "MsPCLatency");
        _clickToPhoton = Find("ClickToPhotonLatency", "MsClickToPhotonLatency");

        if (_app < 0 || _pid < 0 || _frameTime < 0)
            throw new FormatException("Not a PresentMon CSV header: " + headerLine);

        _poolLookup = _pool.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public bool HasGpuBusy => _gpuBusy >= 0;
    public bool HasCpuBusy => _cpuBusy >= 0;
    public bool HasPcLatency => _pcLatency >= 0;
    public bool HasDisplayLatency => _displayLatency >= 0;

    public static bool LooksLikeHeader(string line) => line.StartsWith("Application,", StringComparison.Ordinal);

    public bool TryParse(ReadOnlySpan<char> line, out PresentMonRow row)
    {
        row = default;
        // One spare slot so a row with extra trailing columns doesn't get folded into the last known column.
        Span<Range> ranges = _columnCount <= 63 ? stackalloc Range[64] : new Range[_columnCount + 1];
        int count = line.Split(ranges, ',');
        if (count < _columnCount)
            return false;

        if (!int.TryParse(line[ranges[_pid]], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
            return false;

        float frameTime = ParseFloat(line, ranges, _frameTime);
        if (!(frameTime > 0))
            return false;

        var frame = new FrameSample(
            CpuStartMs: _cpuStart >= 0 ? ParseDouble(line[ranges[_cpuStart]]) : double.NaN,
            FrameTimeMs: frameTime,
            CpuBusyMs: ParseFloat(line, ranges, _cpuBusy),
            CpuWaitMs: ParseFloat(line, ranges, _cpuWait),
            GpuBusyMs: ParseFloat(line, ranges, _gpuBusy),
            GpuWaitMs: ParseFloat(line, ranges, _gpuWait),
            DisplayedTimeMs: ParseFloat(line, ranges, _displayed),
            DisplayLatencyMs: ParseFloat(line, ranges, _displayLatency),
            PcLatencyMs: ParseFloat(line, ranges, _pcLatency),
            ClickToPhotonMs: ParseFloat(line, ranges, _clickToPhoton));

        string mode = _mode >= 0 ? Intern(line[ranges[_mode]]) : string.Empty;
        row = new PresentMonRow(Intern(line[ranges[_app]]), pid, mode, frame);
        return true;
    }

    private static float ParseFloat(ReadOnlySpan<char> line, Span<Range> ranges, int index)
    {
        if (index < 0)
            return float.NaN;
        return float.TryParse(line[ranges[index]], NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : float.NaN;
    }

    private static double ParseDouble(ReadOnlySpan<char> text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : double.NaN;

    private string Intern(ReadOnlySpan<char> text)
    {
        if (_poolLookup.TryGetValue(text, out string? existing))
            return existing;
        string created = text.ToString();
        if (_pool.Count < MaxPooledStrings)
            _pool[created] = created;
        return created;
    }
}
