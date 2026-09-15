using Serilog;

namespace TH.Server.Logic;

// ISessionWorker(Player + LoginSession) 보관소. OutGameLogicEventor가 소유하며 전역 상태가 아니다.
// 등록/제거/조회는 단일 tick 스레드(Prepare/Event/Arrange)에서만 하므로 lock이 없다.
// Work phase 동안은 바뀌지 않으므로 Values 순회가 안전하다.
public sealed class PlayerArchive
{
    private readonly Dictionary<long, ISessionWorker> _bySession = new();

    public int Count => _bySession.Count;

    // Work phase 전체 순회용. Work 동안 archive는 바뀌지 않으므로 열거가 안전하다.
    // IReadOnlyCollection으로 노출해 PlayerWorkExecutor가 바꿀 수 없음을 명시한다.
    // Dictionary.ValueCollection이 IReadOnlyCollection<V>를 구현하므로 추가 할당은 없다.
    public IReadOnlyCollection<ISessionWorker> Values => _bySession.Values;

    public bool TryRegister(long sessionID, ISessionWorker worker)
    {
        if (_bySession.ContainsKey(sessionID))
        {
            Log.Warning("PlayerArchive register skipped — already exists SessionID={ID}", sessionID);
            return false;
        }
        _bySession.Add(sessionID, worker);
        return true;
    }

    public bool Remove(long sessionID) => _bySession.Remove(sessionID);

    // sessionID로 워커를 조회한다.
    public ISessionWorker? Find(long sessionID)
        => _bySession.TryGetValue(sessionID, out var w) ? w : null;

    // sessionID로 특정 타입의 워커를 조회한다. 타입이 다르면 null을 반환한다.
    public T? Find<T>(long sessionID) where T : class, ISessionWorker
        => _bySession.TryGetValue(sessionID, out var w) ? w as T : null;
}
