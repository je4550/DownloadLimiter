namespace DownloadLimit.Core;

// Unlike ArrayPool.Shared, the budget includes both leased and cached storage.
public sealed class BoundedBufferPool
{
    private readonly object _gate = new();
    private readonly Dictionary<int, Stack<byte[]>> _free = new();
    public long MaximumBytes { get; }
    private long _allocated;
    public long AllocatedBytes { get { lock (_gate) return _allocated; } }

    public BoundedBufferPool(long maximumBytes)
    {
        if (maximumBytes < 2048) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        MaximumBytes = maximumBytes;
    }

    public byte[]? Rent(int length)
    {
        if (length <= 0 || length > 65_575) return null;
        int capacity = 2048;
        while (capacity < length) capacity *= 2;
        lock (_gate)
        {
            if (_free.TryGetValue(capacity, out var matching) && matching.Count > 0)
                return matching.Pop();
            if (_allocated + capacity > MaximumBytes)
            {
                foreach (var stack in _free.Values)
                    while (stack.Count > 0 && _allocated + capacity > MaximumBytes)
                        _allocated -= stack.Pop().Length;
            }
            if (_allocated + capacity > MaximumBytes) return null;
            var buffer = new byte[capacity];
            _allocated += capacity;
            return buffer;
        }
    }

    public void Return(byte[] buffer)
    {
        lock (_gate)
        {
            if (!_free.TryGetValue(buffer.Length, out var stack))
                _free.Add(buffer.Length, stack = new Stack<byte[]>());
            stack.Push(buffer);
        }
    }
}
