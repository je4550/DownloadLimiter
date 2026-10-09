using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using DownloadLimit.Core;

// No Windows assembly reference, P/Invoke, sockets, HTTP, or WinDivert loading.
internal static partial class Program
{
    private static void Parsing()
    {
        Check(PacketParser.Parse(Tcp4(0), true).Class == PacketClass.Bypass, "IPv4 ACK was shaped.");
        Check(PacketParser.Parse(Tcp4(200), true).Class == PacketClass.Bulk, "TCP data was exempt.");
        Check(PacketParser.Parse(Udp4(512), false).Class == PacketClass.Priority, "512-byte UDP was not prioritized.");
        Check(PacketParser.Parse(Udp4(513), false).Class == PacketClass.Bulk, "Large UDP was prioritized.");
        byte[] fragment = Udp4(100);
        BinaryPrimitives.WriteUInt16BigEndian(fragment.AsSpan(6), 0x2000);
        Check(PacketParser.Parse(fragment, false).Class == PacketClass.Bulk, "IPv4 fragments bypassed bulk accounting.");
        byte[] ipv6 = Udp6(withExtension: true);
        Check(PacketParser.Parse(ipv6, true).Class == PacketClass.Priority, "IPv6 extension-header UDP parse failed.");
        Check(PacketParser.TryGetLength(ipv6, out int size) && size == ipv6.Length, "IPv6 batch length is incorrect.");
        ipv6[6] = 44; // Same eight-byte extension now represents a fragment.
        Check(PacketParser.Parse(ipv6, true).Class == PacketClass.Bulk, "IPv6 fragments were misclassified as small UDP.");
        Check(PacketParser.Parse(new byte[10], true).Class == PacketClass.Invalid, "Malformed packet was accepted.");
        byte[] invalid = Udp4(100); invalid[24] = invalid[25] = 0;
        Check(PacketParser.Parse(invalid, true).Class == PacketClass.Invalid, "Invalid UDP length was accepted.");
    }

    private static void Exclusions()
    {
        var classifier = new NetworkClassifier([
            NetworkPrefix.Create(IPAddress.Parse("198.51.100.12"), 24),
            NetworkPrefix.Create(IPAddress.Parse("0.0.0.0"), 0)]);
        foreach (string address in new[] { "127.0.0.1", "10.1.2.3", "192.168.10.2", "100.64.1.1", "fe80::1", "fd12::1", "ff02::1", "198.51.100.99", "::ffff:192.168.1.1" })
            Check(!classifier.IsInternet(IPAddress.Parse(address).GetAddressBytes()), $"Local address was eligible: {address}");
        foreach (string address in new[] { "8.8.8.8", "2001:4860:4860::8888" })
            Check(classifier.IsInternet(IPAddress.Parse(address).GetAddressBytes()), $"Internet address was excluded: {address}");
        string filter = classifier.BuildFilter(true);
        Check(filter.Contains("ip.DstAddr") && filter.Contains("ip.SrcAddr") && filter.Contains("ipv6.DstAddr") && filter.Contains("ipv6.SrcAddr"), "Filter did not cover both directions/address families.");
        Check(filter.Contains("not loopback") && filter.Contains("not impostor") && filter.Contains("not tcp or tcp.PayloadLength > 0"), "Required fast-pass filters are absent.");
    }

    private static void Bypass()
    {
        var controller = new BypassController();
        Check(!controller.BeginBypass(0), "Bypass enabled a disabled limiter.");
        controller.Enable();
        Check(controller.BeginBypass(1000), "Bypass did not activate.");
        Check(!controller.TryExpire(900_999), "Bypass ended early.");
        Check(controller.TryExpire(901_000) && controller.Mode == RunMode.Enabled, "15-minute expiry did not restore shaping.");
        controller.BeginBypass(2_000_000);
        controller.BeginBypass(2_500_000);
        Check(!controller.TryExpire(2_900_000), "Restarting bypass did not reset its deadline.");
        Check(controller.TryExpire(3_400_000), "Sleep-sized time advance did not expire bypass.");
        controller.BeginBypass(4_000_000); controller.Disable();
        Check(!controller.TryExpire(5_000_000) && controller.Mode == RunMode.Disabled, "A stale timer re-enabled disabled shaping.");
        controller.Enable(); controller.BeginBypass(6_000_000); controller.Fault();
        Check(!controller.TryExpire(7_000_000) && controller.Mode == RunMode.Faulted, "A stale timer re-enabled a faulted limiter.");
    }

    private static void SettingsAndAbi()
    {
        Check(new AppSettings().IsValid && new AppSettings().StartWithWindows && !new AppSettings().ShapingEnabled,
            "Fresh installs must retain valid defaults with shaping disabled.");
        Check(AppSettings.IsValidLimit(1) && AppSettings.IsValidLimit(10_000) && AppSettings.IsValidLimit(500.001m), "Valid limits were rejected.");
        Check(!AppSettings.IsValidLimit(0) && !AppSettings.IsValidLimit(10_001) && !AppSettings.IsValidLimit(500.0005m), "Invalid limits were accepted.");
        Check(Marshal.SizeOf<PacketAddress>() == 80 && Marshal.OffsetOf<PacketAddress>(nameof(PacketAddress.InterfaceIndex)).ToInt32() == 16, "WinDivert address ABI is incorrect.");
        Check(new PacketAddress { Flags = 1u << 17 }.Outbound, "Outbound flag bit is incorrect.");
    }

    private static void SavedShapingChoice()
    {
        // Fictitious settings only; never read or modify the signed-in user's settings.
        const string legacyJson = "{\"DownloadMbps\":12.5,\"UploadMbps\":4.25,\"StartWithWindows\":true}";
        AppSettings legacy = JsonSerializer.Deserialize<AppSettings>(legacyJson) ?? throw new Exception("Legacy settings were lost.");
        Check(!legacy.ShapingEnabled && legacy.DownloadMbps == 12.5m && legacy.UploadMbps == 4.25m && legacy.StartWithWindows,
            "Upgrading settings without an enable preference must preserve limits and start disabled.");
        foreach (bool enabled in new[] { true, false, true })
        {
            AppSettings selected = legacy with { ShapingEnabled = enabled };
            AppSettings saved = selected with { DownloadMbps = 20m, StartWithWindows = false };
            AppSettings restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(saved)) ??
                throw new Exception("Saved settings were lost.");
            Check(restored.ShapingEnabled == enabled && restored.DownloadMbps == 20m &&
                restored.UploadMbps == legacy.UploadMbps && !restored.StartWithWindows,
                "Saving limits/startup or relaunching discarded the last explicit enable choice.");
        }
    }

}
