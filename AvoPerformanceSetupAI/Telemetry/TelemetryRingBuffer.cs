using System;

namespace AvoPerformanceSetupAI.Telemetry;

/// <summary>
/// Thread-safe fixed-capacity circular buffer for <see cref="TelemetrySample"/> entries.
/// The buffer overwrites the oldest entry when it is full.
/// </summary>
public sealed class TelemetryRingBuffer
{
    private readonly TelemetrySample[] _buf;
    private readonly int               _capacity;
    private int                        _head;  // next write index
    private int                        _count; // items currently stored
    private readonly object            _lock = new();

    /// <param name="capacity">Maximum number of samples to retain. Must be &gt; 0. Default is 30 000 (~120 s at 250 Hz).</param>
    public TelemetryRingBuffer(int capacity = 30_000)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _buf      = new TelemetrySample[capacity];
    }

    /// <summary>Maximum number of samples the buffer can hold.</summary>
    public int Capacity => _capacity;

    /// <summary>Number of samples currently stored (0 … <see cref="Capacity"/>).</summary>
    public int Count
    {
        get { lock (_lock) return _count; }
    }

    /// <summary>
    /// Add a sample. When the buffer is full the oldest entry is silently overwritten.
    /// Safe to call from any thread.
    /// </summary>
    public void Push(in TelemetrySample sample)
    {
        lock (_lock)
        {
            _buf[_head] = sample;
            _head       = (_head + 1) % _capacity;
            if (_count < _capacity) _count++;
        }
    }

    /// <summary>
    /// Returns the most recently pushed sample, or <see langword="default"/> when the buffer
    /// is empty. Safe to call from any thread.
    /// </summary>
    public TelemetrySample ReadLast()
    {
        lock (_lock)
        {
            if (_count == 0) return default;
            var idx = (_head - 1 + _capacity) % _capacity;
            return _buf[idx];
        }
    }

    /// <summary>
    /// Copies the <paramref name="count"/> most recent samples into <paramref name="dest"/> in
    /// chronological order (oldest first). Returns the number of samples actually copied, which
    /// may be less than <paramref name="count"/> when the buffer contains fewer entries.
    /// Safe to call from any thread.
    /// </summary>
    /// <param name="dest">Destination array; must have length ≥ <paramref name="count"/>.</param>
    /// <param name="count">Number of recent samples requested.</param>
    public int CopyTail(TelemetrySample[] dest, int count)
    {
        if (dest is null) throw new ArgumentNullException(nameof(dest));
        lock (_lock)
        {
            var n = Math.Min(Math.Min(count, _count), dest.Length);
            for (int i = 0; i < n; i++)
            {
                // Walk backwards from head, then index forward so result is chronological
                var idx = (_head - n + i + _capacity * 2) % _capacity;
                dest[i] = _buf[idx];
            }
            return n;
        }
    }
}
