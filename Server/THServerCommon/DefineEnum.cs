namespace TH.Common;

// 세션 라이프사이클 단계. 현 PR 에서는 LoggedIn / Disconnecting 만 실제 사용.
// AuthPending 은 Data 서버 RPC 도입 시, InField 는 게임 진입 단계에서 사용 예정.
public enum EPlayerState : byte
{
    Connecting    = 0,
    AuthPending   = 1,
    LoggedIn      = 2,
    InField       = 3,
    Disconnecting = 4,
}
