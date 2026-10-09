using DownloadLimit.Core;

// No Windows assembly reference, P/Invoke, sockets, HTTP, or WinDivert loading.
internal static partial class Program
{
    private static void Accounting()
    {
        var clock = new FakeClock();
        var bucket = new TokenBucket(clock, 1_000_000 / 8.0);
        Check(!bucket.TryConsume(1), "A new bucket must not have startup credit.");
        long bytes = 0;
        for (int i = 0; i < 10_000; i++)
        {
            clock.Advance(0.0001);
            while (bucket.TryConsume(1)) bytes++;
        }
        Check(Math.Abs(bytes - 125_000) <= 1, "1 Mbps must account for 125,000 bytes over one simulated second.");
        double before = bucket.Available;
        Check(!bucket.TryConsume(100), "Rejected consumption must not debit tokens.");
        Check(bucket.Available == before, "Failed consumption changed accounting.");
    }

    private static void OversizedAndChanges()
    {
        var clock = new FakeClock();
        var bucket = new TokenBucket(clock, 125_000);
        clock.Advance(10);
        Check(bucket.Available == 1500, "Idle credit exceeded the normal-packet burst minimum.");
        Check(bucket.TryConsume(60_000), "An oversized packet requires full credit but must remain sendable.");
        Check(bucket.Available == -58_500, "Oversized packet was not charged in full.");
        Check(!bucket.TryConsume(512), "Outstanding packet debt was bypassed.");
        bucket.UpdateRate(62_500);
        Check(bucket.Available == -58_500, "Changing rates discarded debt.");
        clock.Advance(1);
        Check(bucket.Available == 1500, "Credit after time advancement must be clamped.");
        bucket.UpdateRate(125_000_000);
        Check(bucket.Available == 1500, "Increasing the cap minted burst credit.");
    }

    private static async Task Aggregate()
    {
        var clock = new FakeClock();
        var transport = new FakeTransport(clock);
        var settings = new AppSettings(1, 1, false);
        await using var engine = new ShapingEngine(transport, clock, new(), settings);
        for (uint connection = 1; connection <= 8; connection++) transport.Enqueue(Udp4(1200), false, connection);
        await Until(() => transport.ReadCount == 8 && engine.Statistics.QueuedPackets == 8);
        clock.Advance(0.010);
        engine.UpdateLimits(settings);
        await Until(() => engine.Statistics.SentDownloadBytes == 1200);
        Check(engine.Statistics.QueuedPackets == 7, "Connections received separate budgets instead of one aggregate cap.");
        Check(transport.SentBytes(false) <= 1250, "More than 10 ms of initial directional credit was spent.");
    }

    private static async Task IndependentDirections()
    {
        var clock = new FakeClock();
        var transport = new FakeTransport(clock);
        var settings = new AppSettings(1, 1, false);
        await using var engine = new ShapingEngine(transport, clock, new(), settings);
        for (int i = 0; i < 4; i++) { transport.Enqueue(Udp4(1200), false); transport.Enqueue(Udp4(1200), true); }
        await Until(() => transport.ReadCount == 8);
        clock.Advance(0.010);
        engine.UpdateLimits(settings);
        await Until(() => engine.Statistics.SentDownloadBytes == 1200 && engine.Statistics.SentUploadBytes == 1200);
        Check(engine.Statistics.QueuedPackets == 6, "Directions share credit or have multiple budgets.");
    }

    private static async Task QueueBound()
    {
        var clock = new FakeClock();
        var transport = new FakeTransport(clock);
        await using var engine = new ShapingEngine(transport, clock, new(), new(1, 1, false));
        for (int i = 0; i < 2300; i++) transport.Enqueue(Udp4(2000), false);
        await Until(() => transport.ReadCount == 2300);
        var stats = engine.Statistics;
        Check(stats.QueuedPackets == ShapingEngine.MaximumQueuedPackets, "Packet queue bound was not enforced.");
        Check(stats.DroppedPackets == 2300 - ShapingEngine.MaximumQueuedPackets, "Overflow accounting is incorrect.");
        Check(stats.AllocatedBufferBytes <= ShapingEngine.MaximumBufferBytes, "Packet storage exceeded its budget.");
    }

    private static async Task LiveCapChanges()
    {
        var clock = new FakeClock();
        var transport = new FakeTransport(clock);
        var settings = new AppSettings(1, 1, false);
        await using var engine = new ShapingEngine(transport, clock, new(), settings);
        for (int i = 0; i < 8; i++) transport.Enqueue(Udp4(1200), false);
        await Until(() => transport.ReadCount == 8);
        clock.Advance(0.010);
        engine.UpdateLimits(settings);
        await Until(() => engine.Statistics.SentDownloadBytes == 1200);
        settings = settings with { DownloadMbps = 100 };
        engine.UpdateLimits(settings);
        clock.Advance(0.0001);
        engine.UpdateLimits(settings);
        await Until(() => engine.Statistics.SentDownloadBytes >= 2400);
        Check(engine.Statistics.SentDownloadBytes == 2400 && engine.Statistics.QueuedPackets == 6,
            "Changing a cap reset/minted aggregate credit.");
    }

    private static async Task FullQueuePriority()
    {
        var clock = new FakeClock();
        var transport = new FakeTransport(clock);
        var settings = new AppSettings(1, 1, false);
        await using var engine = new ShapingEngine(transport, clock, new(), settings);
        for (int i = 0; i < ShapingEngine.MaximumQueuedPackets; i++) transport.Enqueue(Udp4(2000), false);
        await Until(() => transport.ReadCount == ShapingEngine.MaximumQueuedPackets);
        transport.Enqueue(Udp4(100), false);
        await Until(() => transport.ReadCount == ShapingEngine.MaximumQueuedPackets + 1);
        Check(engine.Statistics.DroppedPackets == 1 && engine.Statistics.QueuedPackets == ShapingEngine.MaximumQueuedPackets,
            "A full queue did not evict one bulk packet to admit priority traffic.");
        clock.Advance(0.004);
        engine.UpdateLimits(settings);
        await Until(() => engine.Statistics.SentDownloadBytes == 100);
        Check(transport.Snapshot()[0].Length == 100, "Bulk queue saturation prevented small-packet priority.");
    }

    private static async Task FullStoragePriority()
    {
        var clock = new FakeClock();
        var transport = new FakeTransport(clock);
        await using var engine = new ShapingEngine(transport, clock, new(), new(1, 1, false));
        for (int i = 0; i < 128; i++) transport.Enqueue(Udp4(40_000), false);
        await Until(() => transport.ReadCount == 128);
        Check(engine.Statistics.AllocatedBufferBytes == ShapingEngine.MaximumBufferBytes,
            "Synthetic storage pressure did not fill the bounded pool.");
        transport.Enqueue(Udp4(100), false);
        await Until(() => transport.ReadCount == 129);
        Check(engine.Statistics.DroppedPackets == 1 && engine.Statistics.QueuedPackets == 128,
            "Storage pressure did not reclaim bulk storage for the priority packet.");
        Check(engine.Statistics.AllocatedBufferBytes <= ShapingEngine.MaximumBufferBytes,
            "Priority admission exceeded the byte bound.");
    }

    private static void PoolBound()
    {
        var pool = new BoundedBufferPool(8 * 1024 * 1024);
        var leased = new List<byte[]>();
        while (pool.Rent(40_000) is byte[] buffer) leased.Add(buffer);
        Check(leased.Count == 128 && pool.AllocatedBytes == pool.MaximumBytes, "Rented capacity was not bounded.");
        Check(pool.Rent(100) is null, "Pool allocated above the live lease budget.");
        foreach (var buffer in leased) pool.Return(buffer);
        byte[] small = pool.Rent(100) ?? throw new Exception("Cached storage was not reclaimed for a smaller rental.");
        Check(pool.AllocatedBytes <= pool.MaximumBytes, "Cached plus active buffers exceeded the budget.");
        pool.Return(small);
        Check(pool.Rent(65_576) is null, "An unsupported packet length was allocated.");
    }

    private static async Task Deadlines()
    {
        var clock = new FakeClock();
        var transport = new FakeTransport(clock);
        var settings = new AppSettings(1, 1, false);
        await using var engine = new ShapingEngine(transport, clock, new(), settings);
        // Oversized-packet debt keeps the queue credit-blocked throughout both
        // deadlines, so this checks expiry rather than a scheduling race.
        clock.Advance(0.012);
        transport.Enqueue(Udp4(40_000), false);
        await Until(() => transport.ReadCount == 1);
        transport.Enqueue(Udp4(1200), false);
        transport.Enqueue(Udp4(1200), false);
        transport.Enqueue(Udp4(512), false);
        transport.Enqueue(Udp4(512), false);
        await Until(() => transport.ReadCount == 5);
        clock.Advance(0.101);
        engine.UpdateLimits(settings);
        await Until(() => engine.Statistics.DroppedPackets == 2);
        Check(engine.Statistics.SentDownloadBytes == 40_000, "Expired priority packets leaked through the cap.");
        clock.Advance(0.150);
        engine.UpdateLimits(settings);
        await Until(() => engine.Statistics.DroppedPackets == 4);
        Check(engine.Statistics.QueuedPackets == 0, "Expired bulk packets were retained.");
    }

    private static async Task Priority()
    {
        var clock = new FakeClock();
        var transport = new FakeTransport(clock);
        var settings = new AppSettings(100, 100, false);
        await using var engine = new ShapingEngine(transport, clock, new(), settings);
        transport.Enqueue(Udp4(1200), false);
        for (int i = 0; i < 50; i++) transport.Enqueue(Udp4(100), false);
        await Until(() => transport.ReadCount == 51);
        clock.Advance(0.001);
        engine.UpdateLimits(settings);
        await Until(() => transport.Snapshot().Length == 51);
        var sent = transport.Snapshot();
        Check(sent[0].Length == 100, "Small UDP did not receive priority over bulk.");
        int bulkIndex = Array.FindIndex(sent, p => p.Length == 1200);
        Check(bulkIndex > 0 && bulkIndex <= 25, "Sustained small packets starved bulk traffic.");
        Check(sent.Sum(p => p.Length) <= 12_500, "Priority packets bypassed aggregate accounting.");
    }

    private static async Task CaptureAge()
    {
        foreach (double pause in new[] { 0.006, 0.030, 0.080 })
        {
            var clock = new FakeClock();
            var transport = new FakeTransport(clock) { BlockReceiving = true };
            await using var engine = new ShapingEngine(transport, clock, new(), new(500, 500, false));
            foreach (bool outbound in new[] { false, true })
            {
                transport.Enqueue(Udp4(1200), outbound);
                transport.Enqueue(Udp4(100), outbound);
            }
            await Until(() => transport.ReceiveStarted);
            clock.Advance(pause);
            transport.ResumeReceiving();
            await Until(() => transport.ReadCount == 4);
            Check(engine.Statistics.DroppedPackets == 0 && transport.Snapshot().Length == 4,
                "A brief scheduling pause discarded sendable bulk or priority traffic.");
            Check(engine.Statistics.SentUploadBytes == 1300 && engine.Statistics.SentDownloadBytes == 1300 &&
                engine.Statistics.AllocatedBufferBytes == 0,
                "Delayed capture changed accounting or allocated unnecessary storage.");
        }
    }

    private static async Task SchedulingPauseCap()
    {
        var clock = new FakeClock();
        var transport = new FakeTransport(clock) { BlockReceiving = true };
        await using var engine = new ShapingEngine(transport, clock, new(), new(1, 1, false));
        foreach (bool outbound in new[] { false, true })
        {
            transport.Enqueue(Udp4(1200), outbound);
            transport.Enqueue(Udp4(100), outbound);
            transport.Enqueue(Udp4(1200), outbound);
        }
        await Until(() => transport.ReceiveStarted);
        clock.Advance(0.080);
        transport.ResumeReceiving();
        await Until(() => transport.ReadCount == 6);
        Check(engine.Statistics.SentDownloadBytes == 1300 && engine.Statistics.SentUploadBytes == 1300 &&
            engine.Statistics.QueuedPackets == 2 && engine.Statistics.DroppedPackets == 0,
            "Capture recovery accumulated excess burst credit or bypassed a directional cap.");
    }

    private static async Task QueuedSchedulingPause()
    {
        var clock = new FakeClock();
        var transport = new FakeTransport(clock);
        var settings = new AppSettings(1, 1, false);
        await using var engine = new ShapingEngine(transport, clock, new(), settings);
        clock.Advance(0.012);
        transport.Enqueue(Udp4(40_000), false);
        await Until(() => transport.ReadCount == 1);
        transport.Enqueue(Udp4(1200), false);
        transport.Enqueue(Udp4(100), false);
        await Until(() => transport.ReadCount == 3);
        transport.Enqueue(Udp4(1200), true);
        await Until(() => transport.ReadCount == 4);
        transport.Enqueue(Udp4(1200), true);
        await Until(() => transport.ReadCount == 5 && engine.Statistics.QueuedPackets == 3);
        clock.Advance(0.030);
        engine.UpdateLimits(settings);
        // This marker can only be sent by the queue worker after the pause.
        await Until(() => engine.Statistics.SentUploadBytes == 2400);
        Check(engine.Statistics.DroppedPackets == 0 && engine.Statistics.QueuedPackets == 2 &&
            engine.Statistics.SentDownloadBytes == 40_000,
            "A scheduling pause expired queued traffic or cleared oversized-packet debt.");
    }

    private static async Task ExemptPackets()
    {
        var clock = new FakeClock();
        var transport = new FakeTransport(clock);
        await using var engine = new ShapingEngine(transport, clock, new(), new(1, 1, false));
        transport.Enqueue(Tcp4(0), true);
        transport.Enqueue(Udp4(1200, "192.168.1.10"), true);
        byte[] control = Udp4(28); control[9] = 1;
        transport.Enqueue(control, true);
        await Until(() => transport.Snapshot().Length == 3);
        Check(engine.Statistics.QueuedPackets == 0, "Exempt packets were queued.");
        Check(engine.Statistics.SentUploadBytes == 0, "Bypass packets consumed the data bucket.");
    }

}
