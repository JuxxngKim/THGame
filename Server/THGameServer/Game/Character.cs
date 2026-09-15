namespace TH.Server.Game;

// 필드(InGame 룸) 캐릭터. 세션 하나에 하나이며 필드에 있는 동안만 존재한다.
// 룸을 맡은 워커 스레드 하나만 접근하므로 동기화 멤버가 없다(룸 single-writer 규약).
// OutGame의 Player와는 별개 객체다. 도메인 간 통신은 패킷 큐로만 하고 직접 참조하지 않는다.
public sealed class Character
{
    // 소속 세션. Session.SessionID와 같은 값이다.
    public long SessionID { get; }

    public Position Position { get; set; }

    public Character(long sessionID)
    {
        SessionID = sessionID;
    }
}
