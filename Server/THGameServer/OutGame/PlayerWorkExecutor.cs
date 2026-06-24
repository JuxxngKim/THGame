using Serilog;
using TH.Common.Network;
using TH.Server.Game;

namespace TH.Server.Logic;

// worker phase 오케스트레이션 + ISessionWorker 집합(PlayerArchive) 소유.
// Player(로그인 완료)와 LoginSession(인증 대기) 을 단일 PlayerArchive 로 통합 관리한다.
//
// 동시성 규약 (아키텍처 문서 §4 유지):
//  - lifecycle 메서드(TryRegister/Remove/Find*)는 Prepare/Event(단일 tick 스레드)에서만 호출.
//  - Run 의 순회/Execute 는 Work phase 에서만 호출.
//  - 즉 archive 변경과 순회가 시간적으로 분리되므로 lock 없이 안전하다.
public sealed class PlayerWorkExecutor
{
    // LoginSession + Player 통합 보관자 — 이 executor 가 소유. tick 스레드에서만 접근.
    private readonly PlayerArchive _archive = new();

    // worker phase 에서 패킷이 없는 워커에 넘길 공유 빈 리스트 (Execute 가 수정하지 않으므로 안전).
    private static readonly List<PacketMessage> EmptyPackets = new();

    // ====================== lifecycle (Prepare/Event phase, 단일 tick 스레드) ======================

    public int Count => _archive.Count;

    // 타입 무관 단일 등록 진입점 — LoginSession 과 Player 모두 이 메서드로 등록한다.
    public bool TryRegister(long sessionID, ISessionWorker worker) => _archive.TryRegister(sessionID, worker);

    // 타입 무관 제거 — 연결 끊김(NetDisconnect)·DOLoginAck 처리 모두 이 메서드 하나로 처리한다.
    public bool Remove(long sessionID) => _archive.Remove(sessionID);

    // 타입별 조회 — 중복 체크 및 DOLoginAck 핸들러에서 사용.
    public Player?       Find(long sessionID)      => _archive.Find<Player>(sessionID);
    public LoginSession? FindLogin(long sessionID) => _archive.Find<LoginSession>(sessionID);

    // 만료(타임아웃) 로그인 세션 정리 — Event phase(단일 tick 스레드)에서만 호출.
    // 만료 세션은 archive 에서 제거하고 해당 네트워크 세션도 종료한다.
    public void RemoveExpiredLogins(long now, long timeoutMs)
    {
        if (_archive.Count == 0) return;

        List<long>? expired = null;
        foreach (var worker in _archive.Values)
        {
            if (worker is not LoginSession session) continue;
            if (now - session.CreatedAt <= timeoutMs) continue;
            (expired ??= new List<long>()).Add(session.SessionID);
        }

        if (expired is null) return;

        foreach (var sessionID in expired)
        {
            _archive.Remove(sessionID);
            NetworkManager.Instance.CloseSession(sessionID);
            Log.Warning("LoginSession timed out SessionID={ID}", sessionID);
        }
    }

    // ====================== worker phase ======================

    // Parallel.ForEach 로 모든 ISessionWorker(LoginSession + Player)를 한 번에 순회한다.
    // Parallel.ForEach 가 동기 반환할 때까지 블로킹되므로 모든 워커 처리가 끝나기 전에는
    // Arrange 가 호출되지 않는다 (barrier 보장).
    public void Run(long tickMs, Dictionary<long, List<PacketMessage>> sessionPackets)
    {
        if (_archive.Count == 0) return;

        Parallel.ForEach(_archive.Values, worker =>
        {
            long sid = worker switch
            {
                Player p        => p.SessionID,
                LoginSession ls => ls.SessionID,
                _               => -1,
            };
            var packets = sid >= 0 && sessionPackets.TryGetValue(sid, out var list) ? list : EmptyPackets;
            worker.Execute(tickMs, packets);
        });
    }
}
