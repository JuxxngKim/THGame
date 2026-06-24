using TH.Server.Game;

namespace TH.Server.Logic;

// worker phase 병렬 실행기. 상태를 갖지 않는 순수 실행기다 — ISessionWorker 집합(PlayerArchive)은
// 호출자(OutGameLogicEventor)가 소유하고, Run 시점에 IReadOnlyCollection 으로 전달받는다.
// (인스턴스를 유지하는 이유: 향후 partitioner 튜닝/워커 스케줄 옵션 등 실행 정책 확장 여지를 남김.)
//
// 동시성 규약 (아키텍처 문서 §4 유지):
//  - Run 의 순회/Execute 는 Work phase 에서만 호출된다.
//  - archive 변경(등록/제거)은 소유자(Eventor)의 Prepare/Event phase 에서만 일어나 Work 와 시간적으로
//    분리되므로, 컬렉션을 reference 로 전달받아 lock 없이 순회해도 안전하다(IReadOnlyCollection 으로
//    변경 불가 계약을 명시).
public sealed class PlayerWorkExecutor
{
    // worker phase 에서 패킷이 없는 워커에 넘길 공유 빈 리스트 (Execute 가 수정하지 않으므로 안전).
    private static readonly List<PacketMessage> EmptyPackets = new();

    // Parallel.ForEach 로 모든 ISessionWorker(LoginSession + Player)를 한 번에 순회한다.
    // Parallel.ForEach 가 동기 반환할 때까지 블로킹되므로 모든 워커 처리가 끝나기 전에는
    // Arrange 가 호출되지 않는다 (barrier 보장).
    public void Run(long tickMs, IReadOnlyCollection<ISessionWorker> workers,
        Dictionary<long, List<PacketMessage>> sessionPackets)
    {
        if (workers.Count == 0) return;

        Parallel.ForEach(workers, worker =>
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
