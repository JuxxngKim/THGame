using TH.Server.Game;

namespace TH.Server.Logic;

// Work phase 병렬 실행기. 상태가 없고, 순회 대상(PlayerArchive)은 호출자가 소유해 Run 시점에 넘겨받는다.
// 인스턴스로 두는 이유는 partitioner 튜닝 같은 실행 정책을 나중에 붙일 자리를 남기기 위함.
// Run은 Work phase에서만 호출되고, archive 변경은 Prepare/Event에서만 일어나 시간이 겹치지 않는다.
// 그래서 lock 없이 순회해도 안전하다. 동시성 규약은 docs/server-logic-architecture.md §4.
public sealed class PlayerWorkExecutor
{
    // 패킷이 없는 워커에 넘기는 공유 빈 리스트. Execute가 수정하지 않으므로 안전하다.
    private static readonly List<PacketMessage> EmptyPackets = new();

    // Parallel.ForEach로 모든 ISessionWorker(LoginSession + Player)를 한 번에 순회한다.
    // Parallel.ForEach는 전부 끝날 때까지 블로킹되므로 워커 처리가 끝나기 전에는 Arrange가 호출되지 않는다(barrier).
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
