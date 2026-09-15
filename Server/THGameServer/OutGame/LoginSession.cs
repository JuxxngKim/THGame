using Serilog;
using Th;
using TH.Server.Common;
using TH.Server.Data;

namespace TH.Server.Logic;

// DB 인증이 끝나기 전까지만 존재하는 임시 로그인 세션. Player보다 먼저 만들어진다.
// Prepare에서 생성·등록되고, Work phase의 Execute가 COLoginReq를 받아 ODLoginReq를 Data 계층으로 보낸다.
// 인증 성공(DOLoginAck) 시 OutGameLogicEventor가 이 세션을 제거하고 그 자리에 Player를 만든다.
// 로그인 핸드셰이크 전체 흐름은 docs/server-logic-architecture.md §2.5.
public sealed class LoginSession : ISessionWorker
{
    public long SessionID { get; }
    public string PID { get; set; } = string.Empty;
    public int LoginVersion { get; set; }
    public bool IsReconnect { get; set; }
    public int LanguageID { get; set; }

    // 타임아웃 판정용 생성 시각(TickMillis). Eventor가 주기적으로 검사해 만료된 세션을 정리한다.
    public long CreatedAt { get; }

    // ODLoginReq 중복 송신 방지. 같은 세션이 COLoginReq를 다시 보내도 처음 한 번만 보낸다.
    private bool _requested;

    // ====================== 패킷 핸들러 테이블 (static 공유) ======================

    private static readonly PacketHandlerTable<LoginSession> Table = new();

    static LoginSession()
    {
        // 핸들러 본문은 msg만 쓰므로 pkt는 무시한다.
        Table.Register<COLoginReq>((int)EMessageID.CoLoginReq, (s, pkt, m) => s.OnCOLoginReq(m));
    }

    // ====================== 생성자 ======================

    public LoginSession(long sessionID, long createdAt)
    {
        SessionID = sessionID;
        CreatedAt = createdAt;
    }

    // ====================== Work phase 진입점 (ISessionWorker) ======================

    // Player.Execute와 같은 패턴. 이 tick에 도착한 패킷을 핸들러 테이블로 dispatch한다.
    public void Execute(long tickMs, List<PacketMessage> packets)
    {
        _ = tickMs;

        foreach (var pkt in packets)
        {
            try
            {
                Table.Dispatch(this, pkt);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "LoginSession handler exception SessionID={ID} PacketID={PID}",
                    SessionID, pkt.PacketID);
            }
        }
    }

    // ====================== 메시지 핸들러 ======================

    // COLoginReq 처리. 데이터 필드는 Prepare에서 이미 채워진 상태로 호출된다.
    // ODLoginReq는 처음 한 번만 Data 계층으로 보낸다.
    // 응답 DOLoginAck는 PacketQueue로 돌아와 다음 tick의 Eventor.Prepare(OnDOLoginAck)가 처리한다.
    private void OnCOLoginReq(COLoginReq msg)
    {
        _ = msg; // 필드는 Prepare에서 이미 this.*에 설정됨.
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
