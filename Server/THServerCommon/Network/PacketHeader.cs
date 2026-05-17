using System.Buffers.Binary;

namespace TH.Common.Network;

public static class PacketHeader
{
    public const int HeaderSize = 8;

    public static void Write(Span<byte> dst, int length, int packetId)
    {
        BinaryPrimitives.WriteInt32LittleEndian(dst, length);
        BinaryPrimitives.WriteInt32LittleEndian(dst[4..], packetId);
    }

    public static bool TryRead(ReadOnlySpan<byte> src, out int length, out int packetId)
    {
        if (src.Length < HeaderSize)
        {
            length = 0;
            packetId = 0;
            return false;
        }

        length = BinaryPrimitives.ReadInt32LittleEndian(src);
        packetId = BinaryPrimitives.ReadInt32LittleEndian(src[4..]);
        return true;
    }
}
