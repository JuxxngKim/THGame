using Google.Protobuf;
using Serilog;
using Th;
using TH.Server.Data;

namespace TH.Server.Logic;

// DB 인증이 끝나기 전의 임시 로그인 컨텍스트. Player 보다 먼저, 인증 성공(DOLoginAck) 전까지만 존재한다.
// COLoginReq(Prepare)에서 생성·등록(+ 데이터 필드 초기화)되고,
// Work phase 의 Execute 가 COLoginReq 패킷을 핸들러 테이블로 dispatch 해 ODLoginReq 를 Data 계층으로 송신한다.
// 인증 성공 시 OutGameLogicEventor(Prepare)가 이 세션을 제거하고 그 자리에 Player 를 생성한다.
// OutGameLogicEventor 가 소유하는 PlayerArchive(ISessionWorker 컬렉션)에 Player 와 함께 보관된다.
public sealed class LoginSession : ISessionWorker
{
    public long SessionID { get; }
    public string PID { get; set; } = string.Empty;
    public int LoginVersion { get; set; }
    public bool IsReconnect { get; set; }
    public int LanguageID { get; set; }

    // 타임아웃 판정용 생성 시각(TickMillis). Eventor 가 주기적으로 검사해 만료 세션을 정리한다.
    public long CreatedAt { get; }

    // ODLoginReq 중복 송신 방지 — 동일 세션에서 COLoginReq 가 재전송되어도 발송은 최초 1회만.
    private bool _requested;

    // ====================== 패킷 핸들러 테이블 (static 공유) ======================

    private static readonly Dictionary<int, Action<LoginSession, ReadOnlyMemory<byte>>> Handlers = new();

    static LoginSession()
    {
        Register<COLoginReq>((int)EMessageID.CoLoginReq, (s, m) => s.OnCOLoginReq(m));
    }

    private static void Register<T>(int packetID, Action<LoginSession, T> handler)
        where T : class, IMessage<T>, new()
    {
        var parser = new MessageParser<T>(() => new T());

        Handlers[packetID] = (session, payload) =>
        {
            T msg;
            try
            {
                msg = parser.ParseFrom(payload.Span);
            }
            catch (InvalidProtocolBufferException ex)
            {
                Log.Warning(ex, "LoginSession packet parse failed SessionID={ID} PacketID={PID}",
                    session.SessionID, packetID);
                return;
            }

            handler(session, msg);
        };
    }

    // ====================== 생성자 ======================

    public LoginSession(long sessionID, long createdAt)
    {
        SessionID = sessionID;
        CreatedAt = createdAt;
    }

    // ====================== worker phase 진입점 (ISessionWorker) ======================

    // Player.Execute 와 동일한 패턴 — 그 tick 에 도착한 패킷을 핸들러 테이블로 dispatch 한다.
    public void Execute(long tickMs, List<PacketMessage> packets)
    {
        _ = tickMs;

        foreach (var pkt in packets)
        {
            if (!Handlers.TryGetValue(pkt.PacketID, out var invoke))
                continue;

            try
            {
                invoke(this, pkt.Payload);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "LoginSession handler exception SessionID={ID} PacketID={PID}",
                    SessionID, pkt.PacketID);
            }
        }
    }

    // ====================== 메시지 핸들러 ======================

    // COLoginReq — Prepare 에서 이미 데이터 필드가 채워진 상태로 호출된다.
    // 최초 1회만 ODLoginReq 를 Data 계층으로 송신한다.
    // 응답 DOLoginAck 는 PacketQueue 로 복귀해 다음 tick 의 Eventor.Prepare(OnDOLoginAck)가 처리한다.
    private void OnCOLoginReq(COLoginReq msg)
    {
        _ = msg; // 필드는 Prepare 에서 이미 this.* 에 설정됨.
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
