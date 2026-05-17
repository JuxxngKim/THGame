namespace TH.Server.Logic;

// List<PacketMessage>에 struct로 저장 — 박싱 없음, 복사 비용 무시 가능.
public readonly struct PacketMessage
{
    public long SessionId { get; }
    public int PacketId { get; }
    public byte[] Payload { get; }

    public PacketMessage(long sessionId, int packetId, byte[] payload)
    {
        SessionId = sessionId;
        PacketId = packetId;
        Payload = payload;
    }
}
