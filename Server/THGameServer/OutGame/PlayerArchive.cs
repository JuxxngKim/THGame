using Serilog;

namespace TH.Server.Logic;

// ISessionWorker 보관자(Player + LoginSession 통합). 등록/제거/조회는 tick 메인 스레드
// (Prepare/Event/Arrange)에서만 호출되므로 lock 없음. Work phase 동안은 불변이라 Values 순회가 안전.
// OutGameLogicEventor 가 소유(composition)하고 lifecycle 을 직접 관리하는 도메인 자료구조 — 전역 상태 아님.
public sealed class PlayerArchive
{
    private readonly Dictionary<long, ISessionWorker> _bySession = new();

    public int Count => _bySession.Count;

    // worker phase 전체 순회용. Work 동안 archive 는 불변(등록/제거는 Prepare/Event 에서만)이므로 열거 안전.
    // IReadOnlyCollection 으로 노출 — 실행기(PlayerWorkExecutor)에 "변경 불가" 계약을 명시한다.
    // (Dictionary.ValueCollection 이 IReadOnlyCollection<V> 를 구현하므로 추가 래핑/할당 없음.)
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

    // sessionID 로 임의 워커를 조회한다.
    public ISessionWorker? Find(long sessionID)
        => _bySession.TryGetValue(sessionID, out var w) ? w : null;

    // sessionID 로 특정 타입의 워커를 조회한다. 타입이 다르면 null 반환.
    public T? Find<T>(long sessionID) where T : class, ISessionWorker
        => _bySession.TryGetValue(sessionID, out var w) ? w as T : null;
}
