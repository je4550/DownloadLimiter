using System.Runtime.InteropServices;

namespace DownloadLimit.Core;

public interface IMonotonicClock
{
    long Timestamp { get; }
    long Frequency { get; }
}

public sealed class SystemMonotonicClock : IMonotonicClock
{
    public long Timestamp => System.Diagnostics.Stopwatch.GetTimestamp();
    public long Frequency => System.Diagnostics.Stopwatch.Frequency;
}

// WinDivert 2.2's NETWORK address ABI is 80 bytes. The union begins at byte 16.
[StructLayout(LayoutKind.Explicit, Size = 80)]
public struct PacketAddress
{
    [FieldOffset(0)] public long Timestamp;
    [FieldOffset(8)] public uint Flags;
    [FieldOffset(12)] public uint Reserved;
    [FieldOffset(16)] public uint InterfaceIndex;
    [FieldOffset(20)] public uint SubInterfaceIndex;
    public readonly bool Outbound => (Flags & (1u << 17)) != 0;
    public readonly bool Loopback => (Flags & (1u << 18)) != 0;
    public readonly bool Impostor => (Flags & (1u << 19)) != 0;
}

public delegate void PacketReceiver(ReadOnlySpan<byte> packet, PacketAddress address);

public interface IPacketTransport : IDisposable
{
    // Blocks until receive shutdown; calls receiver synchronously with borrowed memory.
    void ReceiveLoop(PacketReceiver receiver);
    void Send(ReadOnlySpan<byte> packet, PacketAddress address);
    // Stops new diversion but leaves send available for draining retained packets.
    void StopReceiving();
}

public readonly record struct EngineStatistics(long QueuedBytes, int QueuedPackets,
    long AllocatedBufferBytes, long DroppedPackets, long SentDownloadBytes, long SentUploadBytes);

public sealed record AppSettings(decimal DownloadMbps = 500m, decimal UploadMbps = 500m,
    bool StartWithWindows = true)
{
    public static bool IsValidLimit(decimal limit) => limit >= 1m && limit <= 10_000m &&
        decimal.Round(limit, 3) == limit;
    public bool IsValid => IsValidLimit(DownloadMbps) && IsValidLimit(UploadMbps);
}
