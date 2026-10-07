using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using DownloadLimit.Core;

// No Windows assembly reference, P/Invoke, sockets, HTTP, or WinDivert loading.
internal static partial class Program
{
    private static byte[] Udp4(int size, string destination = "8.8.8.8")
    {
        byte[] packet = new byte[size];
        packet[0] = 0x45; packet[8] = 64; packet[9] = 17;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)size);
        IPAddress.Parse("9.9.9.9").GetAddressBytes().CopyTo(packet, 12);
        IPAddress.Parse(destination).GetAddressBytes().CopyTo(packet, 16);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(24), (ushort)(size - 20));
        return packet;
    }

    private static byte[] Tcp4(int payload)
    {
        byte[] packet = new byte[40 + payload];
        packet[0] = 0x45; packet[8] = 64; packet[9] = 6;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
        IPAddress.Parse("9.9.9.9").GetAddressBytes().CopyTo(packet, 12);
        IPAddress.Parse("8.8.8.8").GetAddressBytes().CopyTo(packet, 16);
        packet[32] = 0x50; packet[33] = 0x10;
        return packet;
    }

    private static byte[] Udp6(bool withExtension)
    {
        int offset = withExtension ? 48 : 40;
        byte[] packet = new byte[offset + 16];
        packet[0] = 0x60; packet[6] = withExtension ? (byte)0 : (byte)17; packet[7] = 64;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), (ushort)(packet.Length - 40));
        IPAddress.Parse("2001:4860:4860::8844").GetAddressBytes().CopyTo(packet, 8);
        IPAddress.Parse("2001:4860:4860::8888").GetAddressBytes().CopyTo(packet, 24);
        if (withExtension) packet[40] = 17;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset + 4), 16);
        return packet;
    }

    private sealed class FakeClock : IMonotonicClock
    {
        private long _timestamp = 1;
        public long Timestamp => Interlocked.Read(ref _timestamp);
        public long Frequency => 1_000_000;
        public void Advance(double seconds) => Interlocked.Add(ref _timestamp, (long)Math.Round(seconds * Frequency));
    }

    private sealed record SentPacket(int Length, bool Outbound, long Timestamp);
    private sealed class FakeTransport(FakeClock clock) : IPacketTransport
    {
        private readonly BlockingCollection<(byte[] Bytes, PacketAddress Address)> _incoming = new();
        private readonly ConcurrentQueue<SentPacket> _sent = new();
        private readonly ManualResetEventSlim _sendRelease = new(false);
        private readonly ManualResetEventSlim _receiveRelease = new(false);
        private int _read, _disposed, _stopCalls, _sendStarted, _receiveStarted;
        public bool BlockSending { get; init; }
        public bool BlockReceiving { get; init; }
        public bool EndReceivingImmediately { get; init; }
        public bool FailSending { get; init; }
        public bool FailReceiving { get; init; }
        public int ReadCount => Volatile.Read(ref _read);
        public int StopCalls => Volatile.Read(ref _stopCalls);
        public bool SendStarted => Volatile.Read(ref _sendStarted) != 0;
        public bool ReceiveStarted => Volatile.Read(ref _receiveStarted) != 0;
        public bool Disposed => Volatile.Read(ref _disposed) != 0;
        public void ResumeReceiving() => _receiveRelease.Set();
        public SentPacket[] Snapshot() => _sent.ToArray();
        public long SentBytes(bool outbound) => Snapshot().Where(p => p.Outbound == outbound).Sum(p => (long)p.Length);
        public void Enqueue(byte[] bytes, bool outbound, uint index = 1) => _incoming.Add((bytes,
            new PacketAddress { Timestamp = clock.Timestamp, InterfaceIndex = index, Flags = outbound ? 1u << 17 : 0 }));
        public void ReceiveLoop(PacketReceiver receiver)
        {
            if (FailReceiving) throw new IOException("Synthetic receive failure.");
            if (EndReceivingImmediately) return;
            foreach (var packet in _incoming.GetConsumingEnumerable())
            {
                Interlocked.Exchange(ref _receiveStarted, 1);
                if (BlockReceiving) _receiveRelease.Wait();
                receiver(packet.Bytes, packet.Address);
                Interlocked.Increment(ref _read);
            }
        }
        public void Send(ReadOnlySpan<byte> packet, PacketAddress address)
        {
            Interlocked.Exchange(ref _sendStarted, 1);
            if (BlockSending) _sendRelease.Wait();
            ObjectDisposedException.ThrowIf(Disposed, this);
            if (FailSending) throw new IOException("Synthetic send failure.");
            _sent.Enqueue(new(packet.Length, address.Outbound, clock.Timestamp));
        }
        public void StopReceiving()
        {
            if (Interlocked.CompareExchange(ref _stopCalls, 1, 0) == 0) _incoming.CompleteAdding();
            _receiveRelease.Set();
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            StopReceiving();
            _sendRelease.Set();
        }
    }
}
