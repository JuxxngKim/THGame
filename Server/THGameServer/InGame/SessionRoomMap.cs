namespace TH.Server.Logic;

// SessionID → RoomID 매핑. 어떤 세션이 어느 룸에 있는지를 나타내며 패킷 라우팅의 기준이 된다.
//
// 동시성 규약(OutGame PlayerArchive와 같은 구조):
//  - 변경(Set/Remove)은 Prepare(단일 tick 스레드)에서만, Work 단계에서는 TryGet으로 읽기만 한다.
//  - 변경과 조회가 같은 시간에 겹치지 않으므로 lock 없이 안전하다.
public sealed class SessionRoomMap
{
    private readonly Dictionary<long, RoomID> _bySession = new();

    public void Set(long sessionID, RoomID roomID) => _bySession[sessionID] = roomID;

    public bool Remove(long sessionID) => _bySession.Remove(sessionID);

    public bool TryGet(long sessionID, out RoomID roomID) => _bySession.TryGetValue(sessionID, out roomID);
}
