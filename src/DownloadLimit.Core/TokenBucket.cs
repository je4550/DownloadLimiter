namespace DownloadLimit.Core;

public sealed class TokenBucket
{
    private readonly IMonotonicClock _clock;
    private readonly int _minimumBurst;
    private long _lastTimestamp;
    private double _tokens;
    public double BytesPerSecond { get; private set; }
    public double Capacity { get; private set; }
    public double Available { get { Refill(); return _tokens; } }

    public TokenBucket(IMonotonicClock clock, double bytesPerSecond, int minimumBurst = 1500)
    {
        _clock = clock;
        _minimumBurst = minimumBurst;
        _lastTimestamp = clock.Timestamp;
        UpdateRate(bytesPerSecond);
    }

    public void UpdateRate(double bytesPerSecond)
    {
        if (!double.IsFinite(bytesPerSecond) || bytesPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        Refill();
        BytesPerSecond = bytesPerSecond;
        Capacity = Math.Max(_minimumBurst, bytesPerSecond * 0.002);
        _tokens = Math.Min(_tokens, Capacity); // Retain debt; never reset on a settings edit.
    }

    private void Refill()
    {
        long now = _clock.Timestamp;
        if (now > _lastTimestamp)
        {
            _tokens = Math.Min(Capacity, _tokens + (now - _lastTimestamp) /
                (double)_clock.Frequency * BytesPerSecond);
            _lastTimestamp = now;
        }
    }

    public double SecondsUntilAvailable(int bytes)
    {
        if (bytes <= 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        Refill();
        // A packet larger than the burst is allowed only with a full bucket.
        // Its entire length is charged, leaving debt for subsequent packets.
        return Math.Max(0, Math.Min(bytes, Capacity) - _tokens) / BytesPerSecond;
    }

    public bool TryConsume(int bytes)
    {
        if (SecondsUntilAvailable(bytes) > 1e-9) return false;
        _tokens -= bytes;
        return true;
    }
}
