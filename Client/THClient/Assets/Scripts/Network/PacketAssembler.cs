using System;

namespace TH.Network
{
    // TCP 바이트 스트림을 [헤더 + payload] 패킷 단위로 자른다.
    // TCP 는 경계가 없어서 패킷 하나가 여러 번에 나눠 오거나, 여러 패킷이 한 번에 붙어 올 수 있다.
    // 소켓과 분리해 둔 이유: 이 자르는 로직이 가장 틀리기 쉬운 부분이라 소켓 없이 테스트하기 위해서다.
    // 스레드 안전하지 않다. 수신 스레드 하나만 쓴다.
    public sealed class PacketAssembler
    {
        // 서버 Session.MaxPacketSize 와 같은 값. 이보다 큰 길이는 스트림이 깨진 것으로 본다.
        public const int MaxPacketSize = 65536;

        // 버퍼를 최대 패킷 크기로 잡으면, 완성 안 된 패킷 하나는 항상 버퍼에 들어가므로 키울 일이 없다.
        private readonly byte[] _buffer = new byte[MaxPacketSize];
        private int _filled;

        // 받은 바이트를 이어 붙이고, 완성된 패킷마다 output 을 호출한다.
        // 길이가 잘못된 헤더를 만나면 false. 스트림이 깨진 것이므로 호출자는 연결을 끊어야 한다.
        public bool Append(byte[] data, int offset, int count, Action<ReceivedPacket> output)
        {
            while (count > 0)
            {
                int n = Math.Min(count, _buffer.Length - _filled);
                Buffer.BlockCopy(data, offset, _buffer, _filled, n);
                _filled += n;
                offset += n;
                count -= n;

                if (!Drain(output))
                    return false;
            }
            return true;
        }

        // 버퍼에서 완성된 패킷을 모두 꺼내고, 남은 조각은 버퍼 앞으로 당긴다.
        private bool Drain(Action<ReceivedPacket> output)
        {
            int consumed = 0;
            while (PacketHeader.TryRead(new ReadOnlySpan<byte>(_buffer, consumed, _filled - consumed),
                       out int length, out int packetID))
            {
                if (length < PacketHeader.HeaderSize || length > MaxPacketSize)
                    return false;

                if (_filled - consumed < length)
                    break;

                int payloadLength = length - PacketHeader.HeaderSize;
                byte[] payload = payloadLength == 0 ? Array.Empty<byte>() : new byte[payloadLength];
                Buffer.BlockCopy(_buffer, consumed + PacketHeader.HeaderSize, payload, 0, payloadLength);
                output(new ReceivedPacket(packetID, payload));
                consumed += length;
            }

            if (consumed > 0)
            {
                _filled -= consumed;
                Buffer.BlockCopy(_buffer, consumed, _buffer, 0, _filled);
            }
            return true;
        }
    }
}
