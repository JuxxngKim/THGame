namespace TH.Server.Logic;

// 룸 생명주기 job — 진입/이탈을 룸 inbox 를 통해 Work phase 로 전달한다.
// SessionRoomMap 의 mutate 는 Prepare 에서 끝나고, 룸 내부 Character 컬렉션 변경은 여기(Work)에서
// 룸 단일 컨슈머가 수행한다 → 룸 상태 single-writer 유지.

// 캐릭터 진입 — 룸이 Character 를 생성/등록하고 Interest.OnEnter 를 호출한다.
public sealed class EnterRoomJob : IRoomJob
{
    private readonly long _sessionID;

    public EnterRoomJob(long sessionID) => _sessionID = sessionID;

    public void Execute(GameRoom room) => room.AddCharacter(_sessionID);
}

// 캐릭터 이탈 — 룸이 Character 를 제거하고 Interest.OnLeave 를 호출한다.
public sealed class LeaveRoomJob : IRoomJob
{
    private readonly long _sessionID;

    public LeaveRoomJob(long sessionID) => _sessionID = sessionID;

    public void Execute(GameRoom room) => room.RemoveCharacter(_sessionID);
}
