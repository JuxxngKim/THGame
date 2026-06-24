namespace TH.Common;

// 세션 라이프사이클 단계. Player 는 DB 인증 성공 후에만 생성되므로 항상 LoggedIn 으로 시작한다.
// (인증 대기 단계는 Player 가 아닌 LoginSession 이 표현 — 별도 상태 enum 불필요.)
// InField 는 게임 진입 단계에서 사용 예정.
public enum EPlayerState : byte
{
    LoggedIn      = 2,
    InField       = 3,
    Disconnecting = 4,
    Entering      = 5,   // 입장 왕복 중(COEnterReq 수신 ~ IOEnterAck 확정 전). 중복 입장 차단용.
}

[Flags]
public enum ELogicEvent : byte
{
    None = 0,
    Prepare = 1 << 0,
    Arrange = 1 << 1,
    Work = 1 << 2,
}
