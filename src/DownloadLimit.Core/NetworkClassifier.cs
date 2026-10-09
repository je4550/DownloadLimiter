using System.Buffers.Binary;
using System.Net;

namespace DownloadLimit.Core;

public readonly record struct NetworkPrefix(int AddressLength, UInt128 First, UInt128 Last)
{
    public static NetworkPrefix Create(IPAddress address, int prefixLength)
    {
        byte[] bytes = address.GetAddressBytes();
        int bits = bytes.Length * 8;
        if (prefixLength < 0 || prefixLength > bits) throw new ArgumentOutOfRangeException(nameof(prefixLength));
        UInt128 value = Read(bytes);
        UInt128 all = bits == 128 ? UInt128.MaxValue : uint.MaxValue;
        UInt128 mask = prefixLength == 0 ? 0 : all << (bits - prefixLength) & all;
        return new(bytes.Length, value & mask, (value & mask) | (all ^ mask));
    }

    public static UInt128 Read(ReadOnlySpan<byte> bytes) => bytes.Length == 4 ?
        BinaryPrimitives.ReadUInt32BigEndian(bytes) : BinaryPrimitives.ReadUInt128BigEndian(bytes);

    public IPAddress ToAddress(UInt128 value)
    {
        byte[] bytes = new byte[AddressLength];
        if (AddressLength == 4) BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)value);
        else BinaryPrimitives.WriteUInt128BigEndian(bytes, value);
        return new IPAddress(bytes);
    }
}

public sealed class NetworkClassifier
{
    private static readonly NetworkPrefix[] Reserved =
    [
        Prefix("0.0.0.0", 8), Prefix("10.0.0.0", 8), Prefix("100.64.0.0", 10),
        Prefix("127.0.0.0", 8), Prefix("169.254.0.0", 16), Prefix("172.16.0.0", 12),
        Prefix("192.168.0.0", 16), Prefix("224.0.0.0", 4), Prefix("240.0.0.0", 4),
        Prefix("::", 128), Prefix("::1", 128), Prefix("fc00::", 7),
        Prefix("fe80::", 10), Prefix("ff00::", 8)
    ];
    public IReadOnlyList<NetworkPrefix> Exclusions { get; }
    private readonly NetworkPrefix[] _ipv4Exclusions, _ipv6Exclusions;

    public NetworkClassifier(IEnumerable<NetworkPrefix>? connectedPrefixes = null)
    {
        Exclusions = Reserved.Concat((connectedPrefixes ?? []).Where(p =>
            !(p.First == 0 && p.Last == (p.AddressLength == 4 ? uint.MaxValue : UInt128.MaxValue))))
            .Distinct().ToArray();
        _ipv4Exclusions = Exclusions.Where(p => p.AddressLength == 4).ToArray();
        _ipv6Exclusions = Exclusions.Where(p => p.AddressLength == 16).ToArray();
    }

    private static NetworkPrefix Prefix(string address, int bits) =>
        NetworkPrefix.Create(IPAddress.Parse(address), bits);

    public bool IsInternet(ReadOnlySpan<byte> remote)
    {
        if (remote.Length is not (4 or 16)) return false;
        // IPv4-mapped addresses are classified by their embedded IPv4 address.
        if (remote.Length == 16 && remote[..10].IndexOfAnyExcept((byte)0) < 0 &&
            remote[10] == 255 && remote[11] == 255) remote = remote[12..];
        UInt128 value = NetworkPrefix.Read(remote);
        // Enumerate concrete arrays: the IReadOnlyList enumerator allocates per packet.
        foreach (NetworkPrefix prefix in remote.Length == 4 ? _ipv4Exclusions : _ipv6Exclusions)
            if (value >= prefix.First && value <= prefix.Last)
                return false;
        return true;
    }

    public string BuildFilter(bool shaping)
    {
        string Remote(string field4, string field6)
        {
            // WinDivert's "not" applies to a field test, not a parenthesized expression.
            // Express each excluded range's complement directly, then intersect them.
            string OutsideRanges(int length, string field) => string.Join(" and ", Exclusions
                .Where(p => p.AddressLength == length)
                .Select(p => $"({field} < {p.ToAddress(p.First)} or {field} > {p.ToAddress(p.Last)})"));
            return $"((ip and {OutsideRanges(4, field4)}) or (ipv6 and {OutsideRanges(16, field6)}))";
        }
        string filter = $"not loopback and not impostor and ((outbound and {Remote("ip.DstAddr", "ipv6.DstAddr")}) or (inbound and {Remote("ip.SrcAddr", "ipv6.SrcAddr")}))";
        if (shaping) filter += " and not icmp and not icmpv6 and (not tcp or tcp.PayloadLength > 0)";
        return filter;
    }
}
