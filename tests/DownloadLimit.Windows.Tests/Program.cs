using System.Net;
using System.Xml.Linq;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using DownloadLimit.Core;
using DownloadLimit.Windows;

// Native helper compilation/evaluation only: no driver handle, packet capture, traffic generation,
// elevation, or Task Scheduler changes. Run explicitly from the repository root.
if (args.Length != 0 && (args.Length != 2 || args[0] != "--check-assembly"))
    throw new ArgumentException("Optional usage: --check-assembly <built app DLL>");
WinDivertTransport.ConfigureLibrary(Path.GetFullPath(".deps/WinDivert/x64"));
NativeLibrary.SetDllImportResolver(typeof(FilterNative).Assembly, (name, _, _) =>
    name == "WinDivert.dll" ? NativeLibrary.Load(Path.GetFullPath(".deps/WinDivert/x64/WinDivert.dll")) : 0);
int passed = 0;
foreach (var classifier in new[]
{
    new NetworkClassifier(),
    LocalNetworks.Snapshot(),
    new NetworkClassifier(new[]
    {
        NetworkPrefix.Create(IPAddress.Parse("203.0.113.20"), 24),
        NetworkPrefix.Create(IPAddress.Parse("2001:db8::1234"), 64),
        NetworkPrefix.Create(IPAddress.Parse("::ffff:192.168.1.2"), 128)
    })
})
    foreach (bool shaping in new[] { false, true })
    {
        WinDivertTransport.ValidateFilter(classifier.BuildFilter(shaping));
        passed++;
        Console.WriteLine($"PASS native filter compilation: shaping={shaping}, exclusions={classifier.Exclusions.Count}");
    }
try
{
    WinDivertTransport.ValidateFilter("not (ip)");
    throw new Exception("Invalid parenthesized negation was accepted.");
}
catch (ArgumentException error) when (error.Message.Contains("character 4"))
{
    passed++;
    Console.WriteLine("PASS invalid filter reports the compiler error position");
}
string xmlPath = Path.Combine(Path.GetTempPath(), $"DownloadLimit-xml-test-{Guid.NewGuid():N}.xml");
try
{
    const string executable = @"C:\Program Files\DownloadLimit\A & B.exe";
    // Fictitious SID with short numeric components; never use a real account here.
    StartupTask.SaveDefinition(xmlPath, executable, "S-1-5-21-1-2-3-1001");
    byte[] bytes = File.ReadAllBytes(xmlPath);
    if (bytes[0] != 0xff || bytes[1] != 0xfe) throw new Exception("Task XML must have a UTF-16LE BOM.");
    var document = XDocument.Load(xmlPath);
    XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    if (document.Declaration?.Encoding != "utf-16" ||
        document.Descendants(ns + "Command").Single().Value != executable ||
        document.Descendants(ns + "RunLevel").Single().Value != "HighestAvailable" ||
        document.Descendants(ns + "LogonType").Single().Value != "InteractiveToken")
        throw new Exception("Task XML encoding, escaped path or interactive elevation is incorrect.");
    passed++;
    Console.WriteLine("PASS startup XML Unicode encoding and escaped executable path");
}
finally { File.Delete(xmlPath); }
foreach (string executable in new[] { "", @"relative\DownloadLimit.exe" })
{
    try
    {
        StartupTask.SaveDefinition(xmlPath, executable, "S-1-5-21-1-2-3-1001");
        throw new Exception("An invalid startup executable path was accepted.");
    }
    catch (ArgumentException) { passed++; }
}
Console.WriteLine("PASS startup definition rejects empty and relative executable paths (2 cases)");

await CheckMonitorAsync();
passed += 4;
CheckDebugPrivacy(args.Length == 2 ? new[] { args[1] } : Array.Empty<string>());
passed += 3 + args.Length / 2;
var reserved = new NetworkClassifier();
foreach (bool ipv6 in new[] { false, true })
    foreach (bool outbound in new[] { false, true })
        foreach (var sample in new[]
        {
            (Remote: ipv6 ? "2001:4860:4860::8888" : "8.8.8.8", Protocol: (byte)17, Payload: 32, Expected: true),
            (Remote: ipv6 ? "fd12::1" : "192.168.1.2", Protocol: (byte)17, Payload: 32, Expected: false),
            (Remote: ipv6 ? "2001:4860:4860::8888" : "8.8.8.8", Protocol: (byte)6, Payload: 100, Expected: true),
            (Remote: ipv6 ? "2001:4860:4860::8888" : "8.8.8.8", Protocol: (byte)6, Payload: 0, Expected: false),
            (Remote: ipv6 ? "2001:4860:4860::8888" : "8.8.8.8", Protocol: ipv6 ? (byte)58 : (byte)1, Payload: 32, Expected: false)
        })
        {
            byte[] packet = MakePacket(ipv6, outbound, sample.Remote, sample.Protocol, sample.Payload);
            var address = new PacketAddress { Flags = (outbound ? 1u << 17 : 0) | (ipv6 ? 1u << 20 : 0) };
            bool matches = FilterNative.Eval(reserved.BuildFilter(true), packet, (uint)packet.Length, ref address);
            if (matches != sample.Expected) throw new Exception($"Native filter mismatch: IPv6={ipv6}, outbound={outbound}, sample={sample}");
            passed++;
        }
Console.WriteLine("PASS native IPv4/IPv6 filter evaluation: upload/download, public data, LAN, TCP ACKs, ICMP controls (20 cases)");
Console.WriteLine($"{passed}/{37 + args.Length / 2} startup/filter/monitor/privacy regression checks passed. No driver handles or network traffic used.");

static void CheckDebugPrivacy(IEnumerable<string> extraAssemblies)
{
    string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Replace('\\', '/');
    string workspace = Path.GetFullPath(".").Replace('\\', '/');
    foreach (string assemblyPath in new[]
    {
        Assembly.GetExecutingAssembly(), typeof(WinDivertTransport).Assembly, typeof(NetworkClassifier).Assembly
    }.Select(assembly => assembly.Location).Concat(extraAssemblies))
    {
        string assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);
        using var binary = File.OpenRead(assemblyPath);
        using var pe = new PEReader(binary);
        var entries = pe.ReadDebugDirectory();
        DebugDirectoryEntry symbols = entries.Single(entry => entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);
        using var provider = pe.ReadEmbeddedPortablePdbDebugDirectoryData(symbols);
        MetadataReader reader = provider.GetMetadataReader();
        int documents = 0;
        foreach (DocumentHandle handle in reader.Documents)
        {
            string name = reader.GetString(reader.GetDocument(handle).Name).Replace('\\', '/');
            if (!name.StartsWith("/_/", StringComparison.Ordinal) || ContainsPrivatePath(name))
                throw new Exception($"Unmapped source path in embedded symbols for {assemblyName}.");
            documents++;
        }
        if (documents == 0) throw new Exception("Privacy regression requires embedded source document metadata.");
        foreach (DebugDirectoryEntry entry in entries.Where(entry => entry.Type == DebugDirectoryEntryType.CodeView))
        {
            string path = pe.ReadCodeViewDebugDirectoryData(entry).Path.Replace('\\', '/');
            if (ContainsPrivatePath(path) || (path.Contains('/') && !path.StartsWith("/_/", StringComparison.Ordinal)))
                throw new Exception($"Unmapped debug symbol path in {assemblyName}.");
        }
        Console.WriteLine($"PASS embedded debug symbols use anonymous source paths: {assemblyName}");
    }
    bool ContainsPrivatePath(string value) =>
        (!string.IsNullOrWhiteSpace(profile) && value.Contains(profile, StringComparison.OrdinalIgnoreCase)) ||
        value.Contains(workspace, StringComparison.OrdinalIgnoreCase) ||
        value.Contains("/Users/", StringComparison.OrdinalIgnoreCase);
}

static async Task CheckMonitorAsync()
{
    var unexpected = new MonitorTransport { ReturnImmediately = true };
    await using (var monitor = new SpeedMonitor(new NetworkClassifier(), unexpected))
    {
        if (!SpinWait.SpinUntil(() => monitor.Error is not null, 2000) || monitor.Error is not IOException)
            throw new Exception("Unexpected monitor termination must report a fault.");
    }
    if (!unexpected.Closed) throw new Exception("Unexpected monitor termination leaked its transport.");

    var failure = new IOException("Synthetic receive failure");
    var faulted = new MonitorTransport { Failure = failure };
    await using (var monitor = new SpeedMonitor(new NetworkClassifier(), faulted))
    {
        if (!SpinWait.SpinUntil(() => monitor.Error is not null, 2000) || !ReferenceEquals(monitor.Error, failure))
            throw new Exception("Receive failures must be published to the tray.");
    }
    if (!faulted.Closed) throw new Exception("Faulted monitor leaked its transport.");

    byte[] download = MakePacket(false, false, "8.8.8.8", 17, 64);
    byte[] upload = MakePacket(true, true, "2001:4860:4860::8888", 17, 32);
    byte[] local = MakePacket(false, false, "192.168.1.2", 17, 64);
    var counting = new MonitorTransport
    {
        Feed = receiver =>
        {
            receiver(download, new());
            receiver(download, new());
            receiver(upload, new() { Flags = 1u << 17 });
            receiver(local, new());
            receiver(ReadOnlySpan<byte>.Empty, new());
        }
    };
    await using (var monitor = new SpeedMonitor(new NetworkClassifier(), counting))
    {
        if (!SpinWait.SpinUntil(() => monitor.Totals.Download == download.Length * 2 &&
            monitor.Totals.Upload == upload.Length, 2000))
            throw new Exception("Monitor must count complete public IP packets in each direction, excluding invalid and LAN packets.");
    }
    if (!counting.Closed || !counting.Completed) throw new Exception("Normal monitor shutdown must release its receiver.");

    var cancelling = new MonitorTransport { StopFails = true };
    var cancelledMonitor = new SpeedMonitor(new NetworkClassifier(), cancelling);
    if (!cancelling.Started.Wait(2000)) throw new Exception("Synthetic monitor receiver did not start.");
    await cancelledMonitor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    if (!cancelling.Closed || !cancelling.Completed || cancelledMonitor.Error is not null)
        throw new Exception("Monitor shutdown must close and finish even if receive shutdown fails.");
    Console.WriteLine("PASS monitor unexpected termination, fault publication, aggregate accounting and cancellation cleanup (4 cases)");
}

static byte[] MakePacket(bool ipv6, bool outbound, string remote, byte protocol, int payload)
{
    int ipLength = ipv6 ? 40 : 20;
    int transportLength = protocol == 6 ? 20 : 8;
    byte[] packet = new byte[ipLength + transportLength + payload];
    packet[0] = ipv6 ? (byte)0x60 : (byte)0x45;
    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(ipv6 ? 4 : 2), (ushort)(ipv6 ? packet.Length - 40 : packet.Length));
    packet[ipv6 ? 6 : 9] = protocol;
    packet[ipv6 ? 7 : 8] = 64;
    byte[] local = IPAddress.Parse(ipv6 ? "2001:db8::1234" : "10.0.0.2").GetAddressBytes();
    byte[] publicAddress = IPAddress.Parse(remote).GetAddressBytes();
    (outbound ? local : publicAddress).CopyTo(packet, ipv6 ? 8 : 12);
    (outbound ? publicAddress : local).CopyTo(packet, ipv6 ? 24 : 16);
    if (protocol == 6) { packet[ipLength + 12] = 0x50; packet[ipLength + 13] = 0x10; }
    else if (protocol == 17) BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(ipLength + 4), (ushort)(8 + payload));
    return packet;
}

internal static class FilterNative
{
    [DllImport("WinDivert.dll", EntryPoint = "WinDivertHelperEvalFilter", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Eval([MarshalAs(UnmanagedType.LPStr)] string filter, byte[] packet,
        uint packetLength, ref PacketAddress address);
}

internal sealed class MonitorTransport : IPacketTransport
{
    private readonly ManualResetEventSlim _release = new(false);
    public readonly ManualResetEventSlim Started = new(false);
    public bool ReturnImmediately { get; init; }
    public bool StopFails { get; init; }
    public Exception? Failure { get; init; }
    public Action<PacketReceiver>? Feed { get; init; }
    public volatile bool Closed, Completed;
    public void ReceiveLoop(PacketReceiver receiver)
    {
        Started.Set();
        try
        {
            if (Failure is not null) throw Failure;
            Feed?.Invoke(receiver);
            if (!ReturnImmediately) _release.Wait();
        }
        finally { Completed = true; }
    }
    public void Send(ReadOnlySpan<byte> packet, PacketAddress address) =>
        throw new Exception("A passive monitor must never reinject packets.");
    public void StopReceiving()
    {
        if (StopFails) throw new IOException("Synthetic shutdown failure");
        _release.Set();
    }
    public void Dispose() { Closed = true; _release.Set(); }
}
