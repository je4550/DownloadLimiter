using DownloadLimit.Core;

// Fake transports and clocks only; these tests do not change OS priorities.
internal static partial class Program
{
    private static async Task CaptureOverload()
    {
        foreach (bool outbound in new[] { false, true })
            foreach (int length in new[] { 100, 1200 })
            {
                var clock = new FakeClock();
                var transport = new FakeTransport(clock) { BlockReceiving = true };
                Exception? failure = null;
                int faults = 0;
                var settings = new AppSettings(500, 500, false, ShapingEnabled: true);
                var engine = new ShapingEngine(transport, clock, new(), settings, onFault: error =>
                {
                    failure = error;
                    Interlocked.Increment(ref faults);
                });
                transport.Enqueue(Udp4(length), outbound);
                await Until(() => transport.ReceiveStarted);
                clock.Advance(0.100);
                transport.ResumeReceiving();
                await Until(() => transport.Disposed && Volatile.Read(ref faults) == 1);
                await engine.StopAsync();
                Check(failure is TimeoutException && engine.IsStopping && transport.StopCalls == 1,
                    "Capture overload retained diversion or failed to report a recoverable error.");
                Check(engine.Statistics.AllocatedBufferBytes == 0 && engine.Statistics.QueuedPackets == 0 &&
                    engine.Statistics.SentDownloadBytes == 0 && engine.Statistics.SentUploadBytes == 0,
                    "Overload allocated storage or counted an unsent packet.");
                Check(settings.ShapingEnabled, "Overload changed the last explicit Enable choice.");
            }
    }

    private static async Task WorkerConfiguration()
    {
        var clock = new FakeClock();
        var transport = new FakeTransport(clock);
        var workers = new HashSet<int>();
        int caller = Environment.CurrentManagedThreadId;
        await using var engine = new ShapingEngine(transport, clock, new(), new(), configureWorker: () =>
        {
            lock (workers) workers.Add(Environment.CurrentManagedThreadId);
        });
        await Until(() => { lock (workers) return workers.Count == 2; });
        lock (workers) Check(!workers.Contains(caller), "Worker initialization ran on the calling thread.");
    }

    private static async Task WorkerConfigurationFailure()
    {
        var clock = new FakeClock();
        var transport = new FakeTransport(clock);
        int faults = 0;
        var engine = new ShapingEngine(transport, clock, new(), new(),
            onFault: _ => Interlocked.Increment(ref faults),
            configureWorker: () => throw new InvalidOperationException("Synthetic worker initialization failure."));
        await Until(() => transport.Disposed && Volatile.Read(ref faults) == 1);
        await engine.StopAsync();
        Check(engine.IsStopping && transport.StopCalls == 1,
            "Worker initialization failure retained packet diversion.");
    }
}
