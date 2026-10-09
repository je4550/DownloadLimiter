using System.Diagnostics;
using System.Net;
using DownloadLimit.Core;

// Borrowed-memory sends and synthetic accounting only; no Windows transport or traffic.
internal static partial class Program
{
    private static async Task DirectSend()
    {
        var clock = new FakeClock();
        var transport = new FakeTransport(clock);
        await using var engine = new ShapingEngine(transport, clock, new(), new(1, 1, false));
        clock.Advance(0.012);
        transport.Enqueue(Udp4(1200), false);
        transport.Enqueue(Udp4(1200), true);
        transport.Enqueue(Udp4(100), false);
        await Until(() => transport.ReadCount == 3);
        var stats = engine.Statistics;
        Check(stats.QueuedPackets == 0 && stats.AllocatedBufferBytes == 0,
            "Eligible idle traffic was copied into managed packet storage.");
        Check(stats.SentDownloadBytes == 1300 && stats.SentUploadBytes == 1200,
            "Direct sends lost full-byte or independent-direction accounting.");
        Check(transport.Snapshot().All(p => p.Timestamp == clock.Timestamp),
            "Direct sends required additional clock advancement.");
        transport.Enqueue(Udp4(1200), false);
        await Until(() => transport.ReadCount == 4);
        Check(engine.Statistics.QueuedPackets == 1 && engine.Statistics.SentDownloadBytes == 1300,
            "The direct-send path bypassed the directional cap.");
    }

    private static async Task DirectSendDebt()
    {
        var clock = new FakeClock();
        var transport = new FakeTransport(clock);
        await using var engine = new ShapingEngine(transport, clock, new(), new(1, 1, false));
        clock.Advance(0.012);
        transport.Enqueue(Udp4(40_000), false);
        await Until(() => transport.ReadCount == 1);
        Check(engine.Statistics.SentDownloadBytes == 40_000 && engine.Statistics.AllocatedBufferBytes == 0,
            "An eligible oversized packet was copied or charged incorrectly.");
        transport.Enqueue(Udp4(100), false);
        await Until(() => transport.ReadCount == 2);
        Check(engine.Statistics.SentDownloadBytes == 40_000 && engine.Statistics.QueuedPackets == 1,
            "A direct oversized send discarded debt for subsequent priority traffic.");
    }

    private static async Task QueuedOrder()
    {
        var clock = new FakeClock();
        var transport = new FakeTransport(clock);
        var settings = new AppSettings(1, 1, false);
        await using var engine = new ShapingEngine(transport, clock, new(), settings);
        transport.Enqueue(Udp4(1200), false);
        await Until(() => transport.ReadCount == 1);
        clock.Advance(0.005);
        transport.Enqueue(Udp4(600), false);
        await Until(() => transport.ReadCount == 2);
        Check(transport.Snapshot().Length == 0 && engine.Statistics.QueuedPackets == 2,
            "A smaller bulk packet bypassed the queued head using direct-send credit.");
        clock.Advance(0.007);
        engine.UpdateLimits(settings);
        await Until(() => engine.Statistics.SentDownloadBytes == 1200);
        Check(transport.Snapshot()[0].Length == 1200 && engine.Statistics.QueuedPackets == 1,
            "Queued bulk order or aggregate accounting changed.");
    }

    private static async Task InFlightOrder()
    {
        var clock = new FakeClock();
        var transport = new FakeTransport(clock) { BlockSending = true };
        var settings = new AppSettings(1, 1, false);
        await using var engine = new ShapingEngine(transport, clock, new(), settings);
        transport.Enqueue(Udp4(1200), false);
        await Until(() => transport.ReadCount == 1);
        clock.Advance(0.012);
        engine.UpdateLimits(settings);
        await Until(() => transport.SendStarted);
        clock.Advance(0.006);
        transport.Enqueue(Udp4(600), false);
        await Until(() => transport.ReadCount == 2);
        Check(engine.Statistics.QueuedPackets == 1,
            "A new receive attempted a direct send while the queued send was still in flight.");
        transport.ResumeSending();
        await Until(() => engine.Statistics.SentDownloadBytes == 1800);
        Check(transport.Snapshot().Select(p => p.Length).SequenceEqual(new[] { 1200, 600 }),
            "A new receive overtook the in-flight queued packet.");
    }

    private static async Task BlockedDirectShutdown()
    {
        var clock = new FakeClock();
        var transport = new FakeTransport(clock) { BlockSending = true };
        var engine = new ShapingEngine(transport, clock, new(), new(1, 1, false));
        clock.Advance(0.012);
        transport.Enqueue(Udp4(1200), false);
        await Until(() => transport.SendStarted);
        Check(transport.ReadCount == 0 && engine.Statistics.AllocatedBufferBytes == 0,
            "The blocked injection was not using borrowed receive memory.");
        var elapsed = Stopwatch.StartNew();
        await engine.StopAsync();
        Check(elapsed.Elapsed < TimeSpan.FromSeconds(1) && transport.Disposed,
            "A blocked direct injection stalled shutdown or retained diversion.");
        Check(engine.Statistics.QueuedPackets == 0 && engine.Statistics.SentDownloadBytes == 0,
            "A cancelled direct injection retained storage or counted unsent bytes.");
    }

    private static async Task DirectSendFailure()
    {
        var clock = new FakeClock();
        var transport = new FakeTransport(clock) { FailSending = true };
        int faults = 0;
        var engine = new ShapingEngine(transport, clock, new(), new(1, 1, false),
            onFault: _ => Interlocked.Increment(ref faults));
        clock.Advance(0.012);
        transport.Enqueue(Udp4(1200), false);
        await Until(() => transport.Disposed && faults == 1);
        await engine.StopAsync();
        Check(engine.IsStopping && engine.Statistics.SentDownloadBytes == 0 &&
            engine.Statistics.AllocatedBufferBytes == 0,
            "A failed direct injection counted bytes, allocated storage or retained capture.");
    }

    private static void ClassificationAllocations()
    {
        var classifier = new NetworkClassifier();
        byte[][] addresses = new[] { "8.8.8.8", "192.168.1.10", "2001:4860:4860::8888",
            "fd12::1", "::ffff:8.8.8.8", "::ffff:192.168.1.10" }
            .Select(address => IPAddress.Parse(address).GetAddressBytes()).ToArray();
        for (int i = 0; i < 1000; i++)
            foreach (byte[] address in addresses) classifier.IsInternet(address);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int publicAddresses = 0;
        for (int i = 0; i < 10_000; i++)
            foreach (byte[] address in addresses)
                if (classifier.IsInternet(address)) publicAddresses++;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(publicAddresses == 30_000, "Allocation optimization changed address classification.");
        Check(allocated == 0, "Address classification allocated storage in the packet path.");
    }
}
