using System.Collections.Concurrent;
using System.Diagnostics;
using DownloadLimit.Core;

// No Windows assembly reference, P/Invoke, sockets, HTTP, or WinDivert loading.
internal static partial class Program
{
    private static async Task Shutdown()
    {
        var clock = new FakeClock();
        var transport = new FakeTransport(clock);
        var engine = new ShapingEngine(transport, clock, new(), new(1, 1, false));
        for (int i = 0; i < 200; i++) transport.Enqueue(Udp4(1200), false);
        await Until(() => transport.ReadCount == 200);
        await engine.StopAsync();
        await engine.StopAsync();
        Check(transport.Disposed && transport.StopCalls == 1, "Shutdown did not release capture exactly once.");
        Check(engine.Statistics.QueuedPackets == 0, "Shutdown left packets queued.");
        Check(transport.Snapshot().Length == 200, "Normal shutdown did not drain retained packets without pacing.");
    }

    private static async Task BlockedShutdown()
    {
        var clock = new FakeClock();
        var transport = new FakeTransport(clock) { BlockSending = true };
        var settings = new AppSettings(500, 500, false);
        var engine = new ShapingEngine(transport, clock, new(), settings);
        transport.Enqueue(Udp4(1200), false);
        await Until(() => transport.ReadCount == 1);
        clock.Advance(0.001);
        engine.UpdateLimits(settings);
        await Until(() => transport.SendStarted);
        var duration = Stopwatch.StartNew();
        await engine.StopAsync();
        Check(duration.Elapsed < TimeSpan.FromSeconds(1), "A blocked injection stalled shutdown.");
        Check(transport.Disposed && engine.Statistics.QueuedPackets == 0, "Stalled send retained capture/queues.");
    }

    private static async Task ReceiveShutdownRace()
    {
        var clock = new FakeClock();
        var transport = new FakeTransport(clock) { BlockReceiving = true };
        var engine = new ShapingEngine(transport, clock, new(), new(1, 1, false));
        transport.Enqueue(Udp4(1200), false);
        await Until(() => transport.ReceiveStarted);
        await engine.StopAsync();
        Check(transport.ReadCount == 1 && transport.Snapshot().Length == 1,
            "A receive callback racing shutdown lost or double-sent its borrowed packet.");
        Check(engine.Statistics.QueuedPackets == 0 && transport.Disposed,
            "The shutdown receive race left capture or queues active.");
    }

    private static async Task FaultCleanup()
    {
        foreach (bool receiving in new[] { true, false })
        {
            var clock = new FakeClock();
            var transport = new FakeTransport(clock) { FailReceiving = receiving, FailSending = !receiving };
            int faults = 0;
            var engine = new ShapingEngine(transport, clock, new(), new(500, 500, false),
                onFault: _ => Interlocked.Increment(ref faults));
            if (!receiving) transport.Enqueue(Tcp4(0), true);
            await Until(() => transport.Disposed && faults == 1);
            await engine.StopAsync();
            Check(engine.IsStopping && engine.Statistics.QueuedPackets == 0, "Fault cleanup did not fail open.");
        }
    }

    private static async Task UnexpectedReceiveTermination()
    {
        var clock = new FakeClock();
        var transport = new FakeTransport(clock) { EndReceivingImmediately = true };
        int faults = 0;
        var engine = new ShapingEngine(transport, clock, new(), new(500, 500, false),
            onFault: _ => Interlocked.Increment(ref faults));
        await Until(() => transport.Disposed && faults == 1);
        await engine.StopAsync();
        Check(engine.IsStopping, "An unexpected receive return left an apparently enabled engine.");
    }

    private static async Task QueuedSendFailure()
    {
        var clock = new FakeClock();
        var transport = new FakeTransport(clock) { FailSending = true };
        int faults = 0;
        var settings = new AppSettings(500, 500, false);
        var engine = new ShapingEngine(transport, clock, new(), settings,
            onFault: _ => Interlocked.Increment(ref faults));
        for (int i = 0; i < 4; i++) transport.Enqueue(Udp4(1200), false);
        await Until(() => transport.ReadCount == 4 && engine.Statistics.QueuedPackets == 4);
        clock.Advance(0.001);
        engine.UpdateLimits(settings);
        await Until(() => transport.Disposed && faults == 1);
        await engine.StopAsync();
        var statistics = engine.Statistics;
        Check(transport.SendStarted && statistics.SentDownloadBytes == 0,
            "A failed queued injection was counted as transmitted data.");
        Check(engine.IsStopping && statistics.QueuedPackets == 0 && statistics.QueuedBytes == 0,
            "A queued sender failure retained packets or active capture.");
        Check(faults == 1, "A single sender failure emitted duplicate notifications.");
    }

    private static async Task ConcurrentShutdown()
    {
        var clock = new FakeClock();
        var transport = new FakeTransport(clock);
        var engine = new ShapingEngine(transport, clock, new(), new(1, 1, false));
        for (int i = 0; i < 200; i++) transport.Enqueue(Udp4(1200), false);
        await Until(() => transport.ReadCount == 200);
        var completions = new ConcurrentBag<Task>();
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
            completions.Add(engine.StopAsync()))));
        Task completion = completions.First();
        Check(completions.All(task => ReferenceEquals(task, completion)),
            "Concurrent callers scheduled separate shutdown operations.");
        await Task.WhenAll(completions);
        Check(transport.Disposed && transport.StopCalls == 1 && engine.Statistics.QueuedPackets == 0,
            "Concurrent shutdown retained capture or queued packets.");
        Check(transport.Snapshot().Length == 200,
            "Concurrent shutdown lost or double-sent retained packets.");
    }

    private static async Task ThrowingFaultObserver()
    {
        var clock = new FakeClock();
        var transport = new FakeTransport(clock) { FailSending = true };
        var settings = new AppSettings(500, 500, false);
        var engine = new ShapingEngine(transport, clock, new(), settings,
            onFault: _ => throw new InvalidOperationException("Synthetic observer failure."));
        int notified = 0;
        engine.Faulted += _ => Interlocked.Increment(ref notified);
        transport.Enqueue(Udp4(1200), false);
        await Until(() => transport.ReadCount == 1);
        clock.Advance(0.001);
        engine.UpdateLimits(settings);
        await Until(() => transport.Disposed);
        await engine.StopAsync();
        Check(engine.IsStopping && engine.Statistics.QueuedPackets == 0,
            "A fault observer interrupted cleanup or retained packet capture.");
        Check(notified == 1, "A failed observer prevented remaining fault notifications.");
    }

}
