using Serilog;

namespace TH.Server.Logic;

// ISessionWorker 보관자(Player + LoginSession 통합). tick 메인 스레드(Prepare/Arrange)에서만 호출되므로 lock 없음.
// OutGameLogicEventor 가 소유(composition)하는 도메인 자료구조 — 전역 상태 아님.
public sealed class PlayerArchive
{
    private readonly Dictionary<long, ISessionWorker> _bySession = new();

    public int Count => _bySession.Count;

    // worker phase 전체 순회용. Work 동안 archive 는 불변(등록/제거는 Prepare/Event 에서만)이므로 열거 안전.
    public Dictionary<long, ISessionWorker>.ValueCollection Values => _bySession.Values;

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
