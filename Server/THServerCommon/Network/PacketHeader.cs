using System.Buffers.Binary;

namespace TH.Common.Network;

public static class PacketHeader
{
    public const int HeaderSize = 8;

    public static void Write(Span<byte> dst, int length, int packetID)
    {
        BinaryPrimitives.WriteInt32LittleEndian(dst, length);
        BinaryPrimitives.WriteInt32LittleEndian(dst[4..], packetID);
    }

    public static bool TryRead(ReadOnlySpan<byte> src, out int length, out int packetID)
    {
        if (src.Length < HeaderSize)
        {
            length = 0;
            packetID = 0;
            return false;
        }

        length = BinaryPrimitives.ReadInt32LittleEndian(src);
        packetID = BinaryPrimitives.ReadInt32LittleEndian(src[4..]);
        return true;
    }
}
