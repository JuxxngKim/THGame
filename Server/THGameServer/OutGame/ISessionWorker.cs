namespace TH.Server.Logic;

// Work phase에서 Execute를 받는 세션 단위 워커의 공통 인터페이스.
// Player(로그인 완료)와 LoginSession(인증 대기)이 구현하고, PlayerArchive가 두 타입을 한 컬렉션에 보관한다.
// 병렬 순회와 Execute 호출은 PlayerWorkExecutor가 담당한다.
public interface ISessionWorker
{
    void Execute(long tickMs, List<PacketMessage> packets);
}
