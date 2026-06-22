using TH.Server.Game;

namespace TH.Server.Logic;

// 기본 관심 관리 — 룸 전원 브로드캐스트. 맵당 인원 캡이 있어 룸 내부 전원 전송으로 충분하다는 도메인 전제.
// 훅(OnEnter/OnMove/OnLeave)은 no-op 이지만 호출 지점은 GameRoom 에 살아 있다 — 추후 AOI 교체를 위한 이음매.
public sealed class BroadcastInterest : IInterestManagement
{
    // 룸 전원이 수신자. (GameRoom.Characters 는 그 룸의 Work 스레드에서만 접근되므로 안전)
    public IReadOnlyList<Character> GetReceivers(GameRoom room, Character source) => room.Characters;

    public void OnEnter(GameRoom room, Character entered) { }
    public void OnMove(GameRoom room, Character moved, Position prev) { }
    public void OnLeave(GameRoom room, Character left) { }
}
