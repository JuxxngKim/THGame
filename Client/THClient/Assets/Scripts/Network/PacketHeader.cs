using System;
using System.Buffers.Binary;

namespace TH.Network
{
    // 패킷 헤더 8바이트: [int32 length LE][int32 packetID LE]. length 는 헤더를 포함한 전체 길이다.
    // 서버 Server/THServerCommon/Network/PacketHeader.cs 를 복제했다(net10.0 이라 Unity 가 참조할 수 없음).
    // 프레이밍을 바꾸면 서버와 클라를 반드시 같이 고친다.
    public static class PacketHeader
    {
        public const int HeaderSize = 8;

        public static void Write(Span<byte> dst, int length, int packetID)
        {
            BinaryPrimitives.WriteInt32LittleEndian(dst, length);
            BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(4), packetID);
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
            packetID = BinaryPrimitives.ReadInt32LittleEndian(src.Slice(4));
            return true;
        }
    }
}
