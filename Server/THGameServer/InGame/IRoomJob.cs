namespace TH.Server.Logic;

// 룸 inbox(JobQueue)에 적재되는 범용 작업 단위. 패킷 전용이 아니다.
// - 네트워크발 InGame 패킷: PacketRoomJob
// - 진입/이탈: EnterRoomJob / LeaveRoomJob
// - 룸 내부에서 발생해 "다음 틱"으로 미루는 작업(스폰/타이머 만료/지연 이벤트 등)도 같은 큐로 넣어
//   동일한 단일 컨슈머 drain 경로로 처리한다.
// Execute 는 GameRoom.Tick(Work phase)에서 그 룸을 잡은 워커 스레드 1개만 호출 — single-writer.
public interface IRoomJob
{
    void Execute(GameRoom room);
}
