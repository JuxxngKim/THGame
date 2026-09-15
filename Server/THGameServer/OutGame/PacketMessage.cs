namespace TH.Server.Logic;

// List<PacketMessage>에 struct로 저장한다. 박싱이 없고 복사 비용은 무시할 수준.
public readonly struct PacketMessage
{
    public long SessionID { get; }
    public int PacketID { get; }
    public byte[] Payload { get; }

    public PacketMessage(long sessionID, int packetID, byte[] payload)
    {
        SessionID = sessionID;
        PacketID = packetID;
        Payload = payload;
    }
}
