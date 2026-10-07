using System.Buffers.Binary;

namespace DownloadLimit.Core;

public enum PacketClass { Bulk, Priority, Bypass, Invalid }

public readonly record struct PacketInfo(int Length, int AddressOffset, int AddressLength,
    byte Protocol, bool Fragment, PacketClass Class);

public static class PacketParser
{
    public static bool TryGetLength(ReadOnlySpan<byte> data, out int length)
    {
        length = 0;
        if (data.Length < 1) return false;
        int version = data[0] >> 4;
        if (version == 4 && data.Length >= 20)
            length = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        else if (version == 6 && data.Length >= 40)
            length = 40 + BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
        else return false;
        return length >= (version == 4 ? 20 : 40) && length <= data.Length;
    }

    public static PacketInfo Parse(ReadOnlySpan<byte> data, bool outbound)
    {
        if (!TryGetLength(data, out int length) || length != data.Length)
            return new(0, 0, 0, 0, false, PacketClass.Invalid);
        int offset, remoteOffset, addressLength;
        byte protocol;
        bool fragment = false;
        if ((data[0] >> 4) == 4)
        {
            offset = (data[0] & 15) * 4;
            if (offset < 20 || offset > length) return new(0, 0, 0, 0, false, PacketClass.Invalid);
            remoteOffset = outbound ? 16 : 12;
            addressLength = 4;
            protocol = data[9];
            fragment = (BinaryPrimitives.ReadUInt16BigEndian(data[6..]) & 0x3fff) != 0;
        }
        else
        {
            offset = 40;
            remoteOffset = outbound ? 24 : 8;
            addressLength = 16;
            protocol = data[6];
            for (int count = 0; protocol is 0 or 43 or 60 or 51 or 44; count++)
            {
                if (count >= 16 || offset + 2 > length)
                    return new(0, 0, 0, 0, false, PacketClass.Invalid);
                byte next = data[offset];
                int extensionLength;
                if (protocol == 44)
                {
                    extensionLength = 8;
                    fragment = true;
                }
                else extensionLength = protocol == 51 ? (data[offset + 1] + 2) * 4 :
                    (data[offset + 1] + 1) * 8;
                if (offset + extensionLength > length)
                    return new(0, 0, 0, 0, false, PacketClass.Invalid);
                offset += extensionLength;
                protocol = next;
                // Later fragments do not begin with a transport header.
                if (fragment) break;
            }
        }
        PacketClass kind = PacketClass.Bulk;
        if (!fragment)
        {
            if (protocol is 1 or 58) kind = PacketClass.Bypass;
            else if (protocol == 6)
            {
                if (offset + 20 > length) return new(0, 0, 0, 0, false, PacketClass.Invalid);
                int tcpHeader = (data[offset + 12] >> 4) * 4;
                if (tcpHeader < 20 || offset + tcpHeader > length)
                    return new(0, 0, 0, 0, false, PacketClass.Invalid);
                if (offset + tcpHeader == length) kind = PacketClass.Bypass;
            }
            else if (protocol == 17)
            {
                if (offset + 8 > length) return new(0, 0, 0, 0, false, PacketClass.Invalid);
                int udpLength = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 4)..]);
                if (udpLength < 8 || offset + udpLength > length)
                    return new(0, 0, 0, 0, false, PacketClass.Invalid);
                if (length <= 512) kind = PacketClass.Priority;
            }
        }
        return new(length, remoteOffset, addressLength, protocol, fragment, kind);
    }
}
