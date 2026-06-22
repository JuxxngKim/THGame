namespace TH.Server.Logic;

// 룸 단위 병렬 실행 — 룸끼리는 병렬, 한 룸은 한 스레드만 잡는다(룸 single-writer 보장).
// Parallel.ForEach 가 동기 반환할 때까지 블로킹되므로 모든 룸 Tick 이 끝나기 전에는 다음 phase 로 넘어가지 않는다.
//
// 한계(인지하고 남김): 이 전략은 "한 틱 = 전체 룸의 배리어"다. 무거운 단일 룸(대규모 인원/전투)이
// 그 틱의 tail-latency 를 지배하면(straggler) 나머지 룸이 끝나도 배리어에서 대기한다.
// 추후 룸을 roomID 로 워커에 핀(shard)해 룸별 독립 진행시키는 ShardedRoomScheduler 로 교체 가능 —
// 이 인터페이스(IRoomScheduler) 뒤이므로 GameRoom/InGameService 시뮬 코드를 건드리지 않고 주입 교체로 끝난다.
public sealed class ParallelRoomScheduler : IRoomScheduler
{
    public void Run(long dtMs, IReadOnlyCollection<GameRoom> rooms)
    {
        if (rooms.Count == 0)
            return;

        Parallel.ForEach(rooms, room => room.Tick(dtMs));
    }
}
