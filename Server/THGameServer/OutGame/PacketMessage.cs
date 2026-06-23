namespace TH.Server.Logic;

// List<PacketMessage>에 struct로 저장 — 박싱 없음, 복사 비용 무시 가능.
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
