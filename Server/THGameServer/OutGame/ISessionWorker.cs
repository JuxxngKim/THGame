namespace TH.Server.Logic;

// Worker phase 에서 Execute 를 받을 수 있는 세션 단위 워커 공통 인터페이스.
// Player(로그인 완료) 와 LoginSession(인증 대기) 이 모두 구현하며,
// OutGameLogicEventor 가 소유하는 PlayerArchive 가 단일 컬렉션으로 두 타입을 보관한다.
// (병렬 순회/Execute 는 PlayerWorkExecutor 가 Work phase 에서 담당.)
public interface ISessionWorker
{
    void Execute(long tickMs, List<PacketMessage> packets);
}
