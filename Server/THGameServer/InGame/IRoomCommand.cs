namespace TH.Server.Logic;

// 크로스도메인(주로 OutGame → InGame) 진입/이탈/포탈 명령.
// InGameService 의 명령 큐에 외부 스레드가 적재하고, Prepare(단일 tick 스레드)에서만 Apply 된다.
// Apply 안에서만 SessionRoomMap 을 mutate 하고, 실제 룸 내부 변경은 룸 inbox(job)로 위임한다.
public interface IRoomCommand
{
    void Apply(SessionRoomMap map, RoomRepository repo);
}

// 진입 — SessionRoomMap 에 등록하고, 대상 룸 inbox 에 EnterRoomJob 을 적재한다.
public sealed class EnterCommand : IRoomCommand
{
    private readonly long _sessionID;
    private readonly RoomID _roomID;

    public EnterCommand(long sessionID, RoomID roomID)
    {
        _sessionID = sessionID;
        _roomID = roomID;
    }

    public void Apply(SessionRoomMap map, RoomRepository repo)
    {
        var room = repo.GetOrCreate(_roomID);
        map.Set(_sessionID, _roomID);               // SessionRoomMap mutate 는 Prepare 에서만
        room.JobQueue.Enqueue(new EnterRoomJob(_sessionID));
    }
}

// 이탈 — SessionRoomMap 에서 현재 룸을 찾아 제거하고, 그 룸 inbox 에 LeaveRoomJob 을 적재한다.
// 세션이 어느 룸에도 없으면 no-op (이미 이탈했거나 진입 전 disconnect).
public sealed class LeaveCommand : IRoomCommand
{
    private readonly long _sessionID;

    public LeaveCommand(long sessionID) => _sessionID = sessionID;

    public void Apply(SessionRoomMap map, RoomRepository repo)
    {
        if (!map.TryGet(_sessionID, out var roomID))
            return;

        map.Remove(_sessionID);
        repo.Find(roomID)?.JobQueue.Enqueue(new LeaveRoomJob(_sessionID));
    }
}
