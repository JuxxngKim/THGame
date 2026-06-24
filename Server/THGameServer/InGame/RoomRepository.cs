namespace TH.Server.Logic;

// RoomID → GameRoom 저장소. 룸 생성/조회. tick 스레드(Prepare)에서만 접근하므로 lock 없음.
public sealed class RoomRepository
{
    private readonly Dictionary<RoomID, GameRoom> _rooms = new();

    // 룸 단위 병렬 실행 대상. Work 동안 룸 집합은 불변(생성/제거는 Prepare 에서만)이므로 열거 안전.
    public IReadOnlyCollection<GameRoom> Rooms => _rooms.Values;

    public GameRoom GetOrCreate(RoomID roomID)
    {
        if (!_rooms.TryGetValue(roomID, out var room))
            _rooms[roomID] = room = new GameRoom(roomID);
        return room;
    }

    public GameRoom? Find(RoomID roomID) => _rooms.TryGetValue(roomID, out var room) ? room : null;
}
