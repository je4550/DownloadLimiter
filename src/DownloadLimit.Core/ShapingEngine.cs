namespace DownloadLimit.Core;

public sealed class ShapingEngine : IAsyncDisposable
{
    public const long MaximumBufferBytes = 8 * 1024 * 1024;
    public const int MaximumQueuedPackets = 2048;
    public const double BulkDeadlineSeconds = 0.020;
    public const double PriorityDeadlineSeconds = 0.005;
    private readonly object _gate = new();
    private readonly object _stopGate = new();
    private readonly IPacketTransport _transport;
    private readonly IMonotonicClock _clock;
    private readonly NetworkClassifier _classifier;
    private readonly BoundedBufferPool _pool;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Direction[] _directions;
    private readonly Task _receiver, _sender;
    private Task? _stopTask;
    private volatile bool _stopping;
    private int _faulted, _queuedPackets, _nextDirection;
    private long _queuedBytes, _dropped, _downloadSent, _uploadSent;

    private sealed record Packet(byte[] Buffer, int Length, PacketAddress Address,
        long CapturedAt, bool Priority);

    private sealed class Direction(IMonotonicClock clock, double rate)
    {
        public readonly Queue<Packet> Bulk = new();
        public readonly Queue<Packet> Priority = new();
        public readonly TokenBucket Bucket = new(clock, rate);
        public readonly TokenBucket PriorityBudget = new(clock, rate * 0.2, 512);
    }

    public event Action<Exception>? Faulted;
    public bool IsStopping => _stopping;

    public ShapingEngine(IPacketTransport transport, IMonotonicClock clock,
        NetworkClassifier classifier, AppSettings settings, BoundedBufferPool? pool = null,
        Action<Exception>? onFault = null)
    {
        if (!settings.IsValid) throw new ArgumentOutOfRangeException(nameof(settings));
        _transport = transport;
        _clock = clock;
        _classifier = classifier;
        _pool = pool ?? new BoundedBufferPool(MaximumBufferBytes);
        _directions = [new(clock, Rate(settings.DownloadMbps)), new(clock, Rate(settings.UploadMbps))];
        Faulted = onFault;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _receiver = Task.Factory.StartNew(() => { ready.Task.GetAwaiter().GetResult(); ReceiveWorker(); }, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _sender = Task.Factory.StartNew(() => { ready.Task.GetAwaiter().GetResult(); SendWorker(); }, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        ready.SetResult();
    }

    private static double Rate(decimal mbps) => (double)mbps * 1_000_000 / 8;

    public void UpdateLimits(AppSettings settings)
    {
        if (!settings.IsValid) throw new ArgumentOutOfRangeException(nameof(settings));
        lock (_gate)
        {
            _directions[0].Bucket.UpdateRate(Rate(settings.DownloadMbps));
            _directions[1].Bucket.UpdateRate(Rate(settings.UploadMbps));
            _directions[0].PriorityBudget.UpdateRate(Rate(settings.DownloadMbps) * 0.2);
            _directions[1].PriorityBudget.UpdateRate(Rate(settings.UploadMbps) * 0.2);
        }
        Signal();
    }

    public EngineStatistics Statistics
    {
        get
        {
            lock (_gate) return new(_queuedBytes, _queuedPackets, _pool.AllocatedBytes,
                Interlocked.Read(ref _dropped), Interlocked.Read(ref _downloadSent),
                Interlocked.Read(ref _uploadSent));
        }
    }

    private void ReceiveWorker()
    {
        try
        {
            _transport.ReceiveLoop(ReceivePacket);
            if (!_stopping) Fail(new IOException("Packet capture ended unexpectedly; shaping was stopped."));
        }
        catch (Exception ex) { if (!_stopping) Fail(ex); }
        finally { Signal(); }
    }

    private void ReceivePacket(ReadOnlySpan<byte> bytes, PacketAddress address)
    {
        PacketInfo info = PacketParser.Parse(bytes, address.Outbound);
        bool exempt = address.Loopback || address.Impostor ||
            info.Class is PacketClass.Bypass or PacketClass.Invalid ||
            !_classifier.IsInternet(bytes.Slice(info.AddressOffset, info.AddressLength));
        if (_stopping || exempt)
        {
            _transport.Send(bytes, address);
            return;
        }
        bool priority = info.Class == PacketClass.Priority;
        lock (_gate)
        {
            // A stop that races capture must also release the borrowed packet.
            if (_stopping)
            {
                // Do not hold the queue lock across a possibly stalled native send.
                goto PassThrough;
            }
            long capturedAt = address.Timestamp > 0 && address.Timestamp <= _clock.Timestamp ?
                address.Timestamp : _clock.Timestamp;
            double age = (_clock.Timestamp - capturedAt) / (double)_clock.Frequency;
            if (age >= (priority ? PriorityDeadlineSeconds : BulkDeadlineSeconds))
            {
                Interlocked.Increment(ref _dropped);
                return;
            }
            // Reserve space for interactive traffic by evicting a queued bulk packet.
            if (_queuedPackets >= MaximumQueuedPackets && (!priority || !EvictBulk()))
            {
                Interlocked.Increment(ref _dropped);
                return;
            }
            byte[]? buffer = _pool.Rent(bytes.Length);
            if (buffer is null && priority && EvictBulk()) buffer = _pool.Rent(bytes.Length);
            if (buffer is null)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }
            bytes.CopyTo(buffer);
            Direction direction = _directions[address.Outbound ? 1 : 0];
            Queue<Packet> queue = priority ? direction.Priority : direction.Bulk;
            bool wasEmpty = queue.Count == 0;
            queue.Enqueue(new(buffer, bytes.Length, address, capturedAt, priority));
            _queuedPackets++;
            _queuedBytes += buffer.Length;
            if (wasEmpty) Signal();
            return;
        }
        PassThrough:
        _transport.Send(bytes, address);
    }

    private bool EvictBulk()
    {
        foreach (Direction direction in _directions)
            if (direction.Bulk.Count > 0)
            {
                Release(Dequeue(direction.Bulk));
                Interlocked.Increment(ref _dropped);
                return true;
            }
        return false;
    }

    private Packet Dequeue(Queue<Packet> queue)
    {
        Packet packet = queue.Dequeue();
        _queuedPackets--;
        _queuedBytes -= packet.Buffer.Length;
        return packet;
    }

    private void Release(Packet packet) => _pool.Return(packet.Buffer);

    private void Expire(Queue<Packet> queue, double deadline)
    {
        while (queue.TryPeek(out Packet? packet) &&
            (_clock.Timestamp - packet.CapturedAt) / (double)_clock.Frequency >= deadline)
        {
            Release(Dequeue(queue));
            Interlocked.Increment(ref _dropped);
        }
    }

    private Packet? Take(Direction direction, out double wait)
    {
        wait = 0.010;
        if (_stopping)
        {
            if (direction.Priority.Count > 0) return Dequeue(direction.Priority);
            return direction.Bulk.Count > 0 ? Dequeue(direction.Bulk) : null;
        }
        Expire(direction.Priority, PriorityDeadlineSeconds);
        Expire(direction.Bulk, BulkDeadlineSeconds);
        bool hasPriority = direction.Priority.TryPeek(out Packet? priority);
        bool hasBulk = direction.Bulk.TryPeek(out Packet? bulk);
        if (!hasPriority && !hasBulk) return null;
        double priorityWait = hasPriority && hasBulk ?
            direction.PriorityBudget.SecondsUntilAvailable(priority!.Length) : 0;
        bool choosePriority = hasPriority && (!hasBulk || priorityWait <= 1e-9);
        Packet candidate = choosePriority ? priority! : bulk!;
        wait = direction.Bucket.SecondsUntilAvailable(candidate.Length);
        if (!direction.Bucket.TryConsume(candidate.Length))
        {
            if (hasPriority && hasBulk && !choosePriority)
                wait = Math.Min(wait, Math.Max(priorityWait,
                    direction.Bucket.SecondsUntilAvailable(priority!.Length)));
            return null;
        }
        if (choosePriority && hasBulk) direction.PriorityBudget.TryConsume(candidate.Length);
        return Dequeue(choosePriority ? direction.Priority : direction.Bulk);
    }

    private void SendWorker()
    {
        try
        {
            while (true)
            {
                Packet? packet = null;
                double delay = 0.010;
                lock (_gate)
                {
                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        int index = (_nextDirection + attempt) % 2;
                        packet = Take(_directions[index], out double wait);
                        delay = Math.Min(delay, wait);
                        if (packet is not null)
                        {
                            _nextDirection = 1 - index;
                            break;
                        }
                    }
                    if (packet is null && _stopping && _queuedPackets == 0) return;
                }
                if (packet is not null)
                {
                    try
                    {
                        _transport.Send(packet.Buffer.AsSpan(0, packet.Length), packet.Address);
                        if (packet.Address.Outbound) Interlocked.Add(ref _uploadSent, packet.Length);
                        else Interlocked.Add(ref _downloadSent, packet.Length);
                    }
                    finally { Release(packet); }
                }
                else _wake.WaitOne(Math.Clamp((int)Math.Ceiling(delay * 1000), 1, 10));
            }
        }
        catch (Exception ex) { if (!_stopping) Fail(ex); }
    }

    private void Signal()
    {
        try { _wake.Set(); }
        catch (ObjectDisposedException) { }
    }

    private void Fail(Exception error)
    {
        if (Interlocked.Exchange(ref _faulted, 1) != 0) return;
        _stopping = true;
        try { _transport.StopReceiving(); } catch { }
        Signal();
        // Cleanup is independent of the UI, including when it is unresponsive.
        _ = StopAsync();
        // Notifications are advisory: a disposed UI or another observer must not
        // fault the worker and make an otherwise completed shutdown throw.
        if (Faulted is { } observers)
            foreach (Action<Exception> observer in observers.GetInvocationList())
                try { observer(error); } catch { }
    }

    public Task StopAsync()
    {
        lock (_stopGate)
        {
            if (_stopTask is not null) return _stopTask;
            _stopping = true;
            // Release new traffic before scheduling any background drain work.
            try { _transport.StopReceiving(); } catch { }
            Signal();
            return _stopTask = Task.Run(StopBlocking);
        }
    }

    private void StopBlocking()
    {
        _stopping = true;
        try { _transport.StopReceiving(); } catch { }
        Signal();
        Task workers = Task.WhenAll(_receiver, _sender);
        try { workers.Wait(TimeSpan.FromMilliseconds(250)); }
        finally
        {
            // Closing cancels outstanding native calls; never wait indefinitely on a worker.
            _transport.Dispose();
            lock (_gate)
                foreach (Direction direction in _directions)
                {
                    while (direction.Priority.Count > 0) Release(Dequeue(direction.Priority));
                    while (direction.Bulk.Count > 0) Release(Dequeue(direction.Bulk));
                }
            Signal();
            _ = workers.ContinueWith(_ => _wake.Dispose(), TaskScheduler.Default);
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
