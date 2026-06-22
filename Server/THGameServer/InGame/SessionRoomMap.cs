namespace TH.Server.Logic;

// SessionId → RoomID 매핑. 어떤 세션이 어느 룸에 있는지(패킷 라우팅의 기준).
//
// 동시성 규약 (OutGame PlayerArchive 와 동형):
//  - mutate(Set/Remove = 입장/퇴장/포탈)는 오직 Prepare(단일 tick 스레드)에서만 일어난다.
//  - Work phase 에서는 read-only(TryGet) 로만 접근한다.
//  - 변경과 조회가 시간적으로 분리되므로 lock 없이 안전하다.
public sealed class SessionRoomMap
{
    private readonly Dictionary<long, RoomID> _bySession = new();

    public void Set(long sessionID, RoomID roomID) => _bySession[sessionID] = roomID;

    public bool Remove(long sessionID) => _bySession.Remove(sessionID);

    public bool TryGet(long sessionID, out RoomID roomID) => _bySession.TryGetValue(sessionID, out roomID);
}
