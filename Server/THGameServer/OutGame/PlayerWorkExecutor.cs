using Serilog;
using TH.Common.Network;
using TH.Server.Game;

namespace TH.Server.Logic;

// worker phase 오케스트레이션 + live Player 집합(PlayerArchive) 소유.
// "어떤 도메인 패킷을 처리하느냐"(OutGameLogicEventor)와 변경 축이 다른 인프라 관심사 —
// "접속 Player 들을 매 tick 어떻게 분산 처리하느냐" 를 담당한다.
//
// 동시성 규약 (아키텍처 문서 §4 유지):
//  - lifecycle 메서드(TryRegister/Remove/Find)는 Prepare(단일 tick 스레드)에서만 호출.
//  - Run 의 순회/Execute 는 Work phase 에서만 호출.
//  - 즉 archive 변경과 순회가 시간적으로 분리되므로 lock 없이 안전하다.
public sealed class PlayerWorkExecutor
{
    // 세션 단위 Player 보관자 — 이 executor 가 소유. tick 스레드에서만 접근.
    private readonly PlayerArchive _archive = new();

    // DB 인증 대기 중인 임시 로그인 세션 — Player 가 되기 전 단계. PlayerArchive 와 분리해 소유.
    // lifecycle(등록/제거)은 Prepare/Event(단일 tick 스레드), 순회는 Work phase 라 lock 불필요.
    private readonly Dictionary<long, LoginSession> _loginSessions = new();

    // worker phase 에서 패킷이 없는 Player 에 넘길 공유 빈 리스트 (Execute 가 수정하지 않으므로 안전).
    private static readonly List<PacketMessage> EmptyPackets = new();

    // ====================== Player lifecycle (Prepare phase, 단일 tick 스레드) ======================

    public int Count => _archive.Count;
    public Player? Find(long sessionID) => _archive.Find(sessionID);
    public bool TryRegister(Player player) => _archive.TryRegister(player);
    public bool Remove(long sessionID) => _archive.Remove(sessionID);

    // ====================== LoginSession lifecycle (Prepare/Event phase, 단일 tick 스레드) ======================

    public LoginSession? FindLogin(long sessionID)
        => _loginSessions.TryGetValue(sessionID, out var s) ? s : null;

    public bool TryRegisterLogin(LoginSession session)
    {
        if (_loginSessions.ContainsKey(session.SessionID))
        {
            Log.Warning("LoginSession register skipped — already exists SessionID={ID}", session.SessionID);
            return false;
        }
        _loginSessions.Add(session.SessionID, session);
        return true;
    }

    public bool RemoveLogin(long sessionID) => _loginSessions.Remove(sessionID);

    // 만료(타임아웃) 로그인 세션 정리 — Event phase(단일 tick 스레드)에서만 호출.
    // 만료 세션은 컬렉션에서 제거하고 해당 네트워크 세션도 종료한다.
    public void RemoveExpiredLogins(long now, long timeoutMs)
    {
        if (_loginSessions.Count == 0) return;

        List<long>? expired = null;
        foreach (var session in _loginSessions.Values)
        {
            if (now - session.CreatedAt <= timeoutMs) continue;
            (expired ??= new List<long>()).Add(session.SessionID);
        }

        if (expired is null) return;

        foreach (var sessionID in expired)
        {
            _loginSessions.Remove(sessionID);
            NetworkManager.Instance.CloseSession(sessionID);
            Log.Warning("LoginSession timed out SessionID={ID}", sessionID);
        }
    }

    // ====================== worker phase ======================

    // worker phase — Prepare 와 Arrange 사이. 접속한 Player 전체를 순회하며 한 워커 스레드가
    // 한 Player(=세션)의 한 tick(입력 패킷 + tick 로직)을 끝까지 담당하고, 끝나면 다음 Player 로
    // 넘어가는 동적 분산 처리. 패킷이 없는 Player 도 tick 로직을 위해 매 tick Execute 된다.
    // Parallel.ForEach 가 동기 반환할 때까지 블로킹되므로 모든 Player 처리가 끝나기 전에는
    // Arrange 가 호출되지 않는다 (barrier 보장).
    public void Run(long tickMs, Dictionary<long, List<PacketMessage>> sessionPackets)
    {
        // 로그인 세션은 패킷 dispatch 가 없고 최초 1회 ODLoginReq 만 발송 — 수가 적어 순차 처리.
        // Work phase 동안 _loginSessions 는 불변(등록/제거는 Prepare/Event)이므로 열거 안전.
        if (_loginSessions.Count > 0)
        {
            foreach (var session in _loginSessions.Values)
                session.Execute(tickMs);
        }

        if (_archive.Count == 0) return;

        Parallel.ForEach(_archive.Players, player =>
        {
            // 그 tick 에 도착한 패킷 (없으면 공유 빈 리스트 — tick 로직만 수행).
            var packets = sessionPackets.TryGetValue(player.SessionID, out var list) ? list : EmptyPackets;
            player.Execute(tickMs, packets);
        });
    }
}
