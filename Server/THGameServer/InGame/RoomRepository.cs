namespace TH.Server.Logic;

// RoomID → GameRoom 저장소. 룸 생성/조회. tick 스레드(Prepare)에서만 접근하므로 lock 없음.
// 룸 생성 시 주입할 IInterestManagement 를 보유 — 모든 룸이 동일 전략을 공유(추후 룸별 차등 가능).
public sealed class RoomRepository
{
    private readonly Dictionary<RoomID, GameRoom> _rooms = new();
    private readonly IInterestManagement _interest;

    public RoomRepository(IInterestManagement interest) => _interest = interest;

    // 룸 단위 병렬 실행 대상. Work 동안 룸 집합은 불변(생성/제거는 Prepare 에서만)이므로 열거 안전.
    public IReadOnlyCollection<GameRoom> Rooms => _rooms.Values;

    public GameRoom GetOrCreate(RoomID roomID)
    {
        if (!_rooms.TryGetValue(roomID, out var room))
            _rooms[roomID] = room = new GameRoom(roomID, _interest);
        return room;
    }

    public GameRoom? Find(RoomID roomID) => _rooms.TryGetValue(roomID, out var room) ? room : null;
}
