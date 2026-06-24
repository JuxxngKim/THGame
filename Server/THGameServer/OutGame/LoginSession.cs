using Th;
using TH.Server.Data;

namespace TH.Server.Logic;

// DB 인증이 끝나기 전의 임시 로그인 컨텍스트. Player 보다 먼저, 인증 성공(DOLoginAck) 전까지만 존재한다.
// COLoginReq(Prepare)에서 생성·등록되고, Work phase 의 Execute 가 최초 1회 ODLoginReq 를 Data 계층으로 송신한다.
// 인증 성공 시 OutGameLogicEventor(Prepare)가 이 세션을 제거하고 그 자리에 Player 를 생성한다.
// PlayerArchive 에는 넣지 않는다 — PlayerWorkExecutor 가 별도 컬렉션으로 소유.
public sealed class LoginSession
{
    public long SessionID { get; }
    public string PID { get; set; } = string.Empty;
    public int LoginVersion { get; set; }
    public bool IsReconnect { get; set; }
    public int LanguageID { get; set; }

    // 타임아웃 판정용 생성 시각(Unix ms). Eventor 가 주기적으로 검사해 만료 세션을 정리한다.
    public long CreatedAt { get; }

    // ODLoginReq 중복 송신 방지 — Execute 는 매 tick 호출되지만 실제 발송은 최초 1회만.
    private bool _requested;

    public LoginSession(long sessionID, long createdAt)
    {
        SessionID = sessionID;
        CreatedAt = createdAt;
    }

    // worker phase 진입점 — 최초 1회만 ODLoginReq 를 Data 계층으로 송신한다.
    // 응답 DOLoginAck 는 PacketQueue 로 복귀해 다음 tick 의 Eventor.Prepare(OnDOLoginAck)가 처리한다.
    public void Execute(long tickMs)
    {
        _ = tickMs;
        if (_requested) return;
        _requested = true;

        var odReq = new ODLoginReq
        {
            MessageID   = EMessageID.OdLoginReq,
            PID         = PID,
            LogKey      = 0,
            UpdateDate  = new MDateTime(),
            IsReconnect = IsReconnect,
            ServerID    = 0,
            LanguageID  = LanguageID,
        };
        DBService.Instance.Send(SessionID, (int)EMessageID.OdLoginReq, odReq);
    }
}
