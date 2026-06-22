namespace TH.Server.Logic;

// 룸 실행 전략 추상화 — "룸들을 매 틱 어떻게 돌릴 것인가". InGameService 의 Work phase 가 이 뒤로만 실행한다.
// 현재 구현은 ParallelRoomScheduler 하나. 교체 경계로 둔 이유는 IRoomScheduler 구현체 주석 참조.
public interface IRoomScheduler
{
    void Run(long dtMs, IReadOnlyCollection<GameRoom> rooms);
}
