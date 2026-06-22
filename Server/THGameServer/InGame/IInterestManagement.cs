using TH.Server.Game;

namespace TH.Server.Logic;

// "이벤트를 누구에게 보낼지"만 책임지는 교체 가능 경계. 룸 시뮬 코드는 이 인터페이스 뒤로만 브로드캐스트한다.
// 현재 구현은 BroadcastInterest(룸 전원). 추후 GridAoiInterest 를 추가해도 GameRoom 을 건드리지 않고
// 주입 교체만으로 끝나도록 OnEnter/OnMove/OnLeave 호출 지점을 지금부터 살려둔다(no-op 이어도 호출은 발생).
public interface IInterestManagement
{
    // source 가 일으킨 이벤트를 받을 대상 목록. BroadcastInterest 는 룸 전원을 반환.
    IReadOnlyList<Character> GetReceivers(GameRoom room, Character source);

    void OnEnter(GameRoom room, Character entered);
    void OnMove(GameRoom room, Character moved, Position prev);
    void OnLeave(GameRoom room, Character left);
}
