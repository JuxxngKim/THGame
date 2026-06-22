namespace TH.Server.Logic;

// 네트워크발 InGame 패킷 1건을 해당 룸에서 처리하는 job. IRoomJob 구현 중 하나일 뿐이다.
// 패킷 라우팅(Prepare)에서 SessionRoomMap 조회로 룸을 찾아 이 job 을 룸 inbox 에 적재한다.
public sealed class PacketRoomJob : IRoomJob
{
    private readonly PacketMessage _packet;

    public PacketRoomJob(PacketMessage packet) => _packet = packet;

    public void Execute(GameRoom room) => room.HandlePacket(_packet);
}
