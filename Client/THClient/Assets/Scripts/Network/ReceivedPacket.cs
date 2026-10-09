namespace TH.Network
{
    // 수신 스레드가 잘라낸 패킷 하나. Payload 는 이 패킷만의 복사본이라 다른 스레드로 넘겨도 안전하다.
    public readonly struct ReceivedPacket
    {
        public readonly int PacketID;
        public readonly byte[] Payload;

        public ReceivedPacket(int packetID, byte[] payload)
        {
            PacketID = packetID;
            Payload = payload;
        }
    }
}
