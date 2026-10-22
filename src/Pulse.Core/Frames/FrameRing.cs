namespace Pulse.Core.Frames;

/// <summary>Fixed-size, thread-safe circular buffer of recent frames for one process.</summary>
public sealed class FrameRing
{
    private readonly FrameSample[] _buffer;
    private readonly Lock _lock = new();
    private int _next;
    private int _count;

    public FrameRing(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _buffer = new FrameSample[capacity];
    }

    public int Capacity => _buffer.Length;

    public int Count
    {
        get { lock (_lock) return _count; }
    }

    public void Add(in FrameSample frame)
    {
        lock (_lock)
        {
            _buffer[_next] = frame;
            _next = (_next + 1) % _buffer.Length;
            if (_count < _buffer.Length)
                _count++;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _next = 0;
            _count = 0;
        }
    }

    /// <summary>Copies the newest frames, oldest first, into <paramref name="destination"/>. Returns how many were copied.</summary>
    public int CopyLast(Span<FrameSample> destination)
    {
        lock (_lock)
        {
            int n = Math.Min(destination.Length, _count);
            int start = (_next - n + _buffer.Length) % _buffer.Length;
            for (int i = 0; i < n; i++)
                destination[i] = _buffer[(start + i) % _buffer.Length];
            return n;
        }
    }

    /// <summary>
    /// Copies frames whose start time is within <paramref name="windowMs"/> of the newest frame, oldest first.
    /// Frames without a start time are treated as in-window.
    /// </summary>
    public int CopyWindow(double windowMs, Span<FrameSample> destination)
    {
        lock (_lock)
        {
            if (_count == 0)
                return 0;

            int newestIndex = (_next - 1 + _buffer.Length) % _buffer.Length;
            double newest = _buffer[newestIndex].CpuStartMs;
            int n = 0;
            int limit = Math.Min(_count, destination.Length);
            while (n < limit)
            {
                ref readonly FrameSample frame = ref _buffer[(newestIndex - n + _buffer.Length) % _buffer.Length];
                if (!double.IsNaN(newest) && !double.IsNaN(frame.CpuStartMs) && newest - frame.CpuStartMs > windowMs)
                    break;
                n++;
            }

            int start = (newestIndex - n + 1 + _buffer.Length) % _buffer.Length;
            for (int i = 0; i < n; i++)
                destination[i] = _buffer[(start + i) % _buffer.Length];
            return n;
        }
    }
}
