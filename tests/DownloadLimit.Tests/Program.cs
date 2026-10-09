using System.Diagnostics;

// No Windows assembly reference, P/Invoke, sockets, HTTP, or WinDivert loading.
internal static partial class Program
{
    private static async Task<int> Main()
    {
        (string Name, Func<Task> Run)[] tests =
        [
            ("Decimal Mbps and fractional-byte accounting", Sync(Accounting)),
            ("Oversized packet debt and rate-edit credit clamp", Sync(OversizedAndChanges)),
            ("Aggregate cap across simulated applications/interfaces", Aggregate),
            ("Independent upload and download budgets", IndependentDirections),
            ("Idle traffic sends from borrowed memory within each cap", DirectSend),
            ("Direct sends preserve oversized packet debt", DirectSendDebt),
            ("New bulk traffic cannot overtake a queued packet", QueuedOrder),
            ("New traffic cannot overtake an in-flight queued send", InFlightOrder),
            ("Live cap changes preserve aggregate credit", LiveCapChanges),
            ("Packet queue bound under sustained input", QueueBound),
            ("Priority admission evicts bulk at the packet bound", FullQueuePriority),
            ("Priority admission reclaims storage at the byte bound", FullStoragePriority),
            ("Leased plus cached packet storage bound", Sync(PoolBound)),
            ("Bulk and interactive queue residence deadlines", Deadlines),
            ("Brief capture delays retain bulk and priority packets", CaptureAge),
            ("Capture recovery preserves small bursts and directional caps", SchedulingPauseCap),
            ("Brief queue delays preserve packets and oversized debt", QueuedSchedulingPause),
            ("Severe capture delays release diversion independently of UI", CaptureOverload),
            ("Packet worker initialization runs on both dedicated workers", WorkerConfiguration),
            ("Packet worker initialization failure releases diversion", WorkerConfigurationFailure),
            ("Small UDP priority and bulk non-starvation", Priority),
            ("TCP ACKs, controls and local traffic bypass", ExemptPackets),
            ("Shutdown drains once and releases capture", Shutdown),
            ("Shutdown racing a borrowed receive releases its packet", ReceiveShutdownRace),
            ("Blocked send cancellation and bounded shutdown", BlockedShutdown),
            ("Blocked direct send cancellation and bounded shutdown", BlockedDirectShutdown),
            ("Direct send failures release capture without counting bytes", DirectSendFailure),
            ("Receive/send failures automatically release capture", FaultCleanup),
            ("Queued reinjection failures release buffers and capture", QueuedSendFailure),
            ("Fault observer errors cannot interrupt networking cleanup", ThrowingFaultObserver),
            ("Concurrent shutdown callers share one completion", ConcurrentShutdown),
            ("Unexpected receive termination automatically fails open", UnexpectedReceiveTermination),
            ("IPv4, IPv6 extensions, fragments and malformed packets", Sync(Parsing)),
            ("Remote-address LAN filtering, mapped IPv4 and no /0 exclusion", Sync(Exclusions)),
            ("IPv4/IPv6 address classification allocates no per-packet storage", Sync(ClassificationAllocations)),
            ("Bypass expiry, reset, sleep time and stale-timer cancellation", Sync(Bypass)),
            ("Last Enable/Disable choice survives settings round trips and legacy upgrades", Sync(SavedShapingChoice)),
            ("Settings validation and native address layout", Sync(SettingsAndAbi))
        ];
        int failures = 0;
        foreach (var test in tests)
        {
            try { await test.Run().WaitAsync(TimeSpan.FromSeconds(10)); Console.WriteLine($"PASS {test.Name}"); }
            catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {error}"); }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} offline tests passed. Live Windows/driver/network behavior was not tested.");
        return failures == 0 ? 0 : 1;
    }

    private static Func<Task> Sync(Action action) => () => { action(); return Task.CompletedTask; };
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Until(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(4)) throw new TimeoutException("Synthetic worker did not reach its expected state.");
            await Task.Delay(1);
        }
    }

}
