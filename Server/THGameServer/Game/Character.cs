namespace TH.Server.Game;

// 필드(InGame 룸) 캐릭터 엔티티 — sessionId 스코프. 한 룸을 잡은 워커 스레드 1개가 단독 접근하므로
// 동기화 멤버 없음(룸 single-writer 규약). OutGame 의 Player(영속, 메인)와 분리된 개념으로,
// 필드에 진입한 동안에만 존재한다. 크로스 도메인 통신은 job/command queue 로만(직접 참조 금지).
public sealed class Character
{
    // 소속 세션. (기존 Session.SessionId 와 동일 값을 보관 — 신규 멤버이므로 대문자 ID 표기)
    public long SessionID { get; }

    public Position Position { get; set; }

    public Character(long sessionID)
    {
        SessionID = sessionID;
    }
}
