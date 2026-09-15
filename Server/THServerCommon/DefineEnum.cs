namespace TH.Common;

// 세션 상태. Player는 DB 인증 성공 후에만 생성되므로 항상 LoggedIn으로 시작한다.
// 인증 대기 단계는 Player가 아니라 LoginSession이 담당하므로 별도 상태가 없다.
public enum EPlayerState : byte
{
    LoggedIn      = 2,
    InField       = 3,
    Disconnecting = 4,
    Entering      = 5,   // 입장 처리 중(COEnterReq 수신 후 IOEnterAck 전). 중복 입장을 막는다.
}

[Flags]
public enum ELogicEvent : byte
{
    None = 0,
    Prepare = 1 << 0,
    Arrange = 1 << 1,
    Work = 1 << 2,
}
