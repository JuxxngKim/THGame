using Google.Protobuf;
using Serilog;
using Th;
using TH.Common;
using TH.Common.Network;
using TH.Server.Common;
using TH.Server.Data;
using TH.Server.Logic;

namespace TH.Server.Game;

// 세션 하나에 하나인 Player. tick 스레드 또는 worker 스레드 하나만 접근하므로 동기화 멤버가 없다.
// 패킷 핸들러 테이블은 전 Player가 static으로 공유하고, 처리 본문은 인스턴스 메서드다.
public sealed class Player : ISessionWorker
{
    public long SessionID { get; }
    public long AccountID { get; set; }
    public string PID { get; set; } = string.Empty;
    public EPlayerState State { get; set; }

    // Player는 DB 인증 성공(DOLoginAck) 후에만 만들어지므로 항상 로그인 완료 상태로 시작한다.
    public Player(long sessionID)
    {
        SessionID = sessionID;
        State = EPlayerState.LoggedIn;
    }

    // ====================== 패킷 핸들러 테이블 (static 공유) ======================

    // packetID → dispatch 델리게이트(공통 테이블). static 생성자에서만 채우고 이후 읽기 전용.
    private static readonly PacketHandlerTable<Player> Table = new();

    static Player()
    {
        // 로그인 패킷은 Player가 처리하지 않는다. COLoginReq는 LoginSession, DOLoginAck는 OutGameLogicEventor(Prepare)가 처리한다.
        // 핸들러 본문은 msg만 쓰므로 pkt는 무시한다.
        Table.Register<COGetPlayerReq>((int)EMessageID.CoGetPlayerReq, (p, pkt, m) => p.OnCOGetPlayerReq(m));
        Table.Register<COEnterReq>((int)EMessageID.CoEnterReq, (p, pkt, m) => p.OnCOEnterReq(m));
        Table.Register<IOEnterAck>((int)EMessageID.IoEnterAck, (p, pkt, m) => p.OnIOEnterAck(m));

        // 세션 종료 흐름. NetDisconnect로 DB 세션 종료 저장을 시작하고 DOExitGameSessionAck로 완료를 받는다.
        // 핸들러 본문은 인스턴스 상태(State/AccountID/PID)만 쓰므로 pkt/msg는 무시한다.
        Table.Register<NetDisconnect>((int)EMessageID.NetDisconnect, (p, pkt, m) => p.OnNetDisconnect());
        Table.Register<DOExitGameSessionAck>((int)EMessageID.DoExitGameSessionAck, (p, pkt, m) => p.OnDOExitGameSessionAck());
    }

    // ====================== worker phase 진입점 ======================

    // 한 tick 실행. worker 스레드 하나가 이 Player를 맡는다.
    // 그 tick에 도착한 packets를 도착 순서대로 처리한 뒤 tick 단위 로직을 돈다.
    // 규약: 자기 자신과 자기 세션(Send) 외의 전역 상태나 다른 Player를 바꾸지 않는다.
    //       다른 Player를 바꿔야 하면 Arrange 단계(단일 tick 스레드)로 미룬다.
    public void Execute(long tickMs, List<PacketMessage> packets)
    {
        // 1) 입력 패킷 처리(도착 순서 유지). 한 패킷의 실패가 다음 패킷을 막지 않도록 패킷마다 try/catch.
        foreach (var pkt in packets)
        {
            try
            {
                if (!Table.Dispatch(this, pkt))
                    Log.Debug("Unregistered player packet dropped SessionID={ID} PacketID={PID}", SessionID, pkt.PacketID);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Player handler exception SessionID={ID} PacketID={PID}", SessionID, pkt.PacketID);
            }
        }

        // 2) tick 단위 Player 로직(버프 만료 / 타이머 등). 아직 구현 전.
        _ = tickMs;
    }

    // 자기 세션으로 응답을 보내는 헬퍼. Session.Send는 스레드 안전해서 worker에서 불러도 된다.
    private void Send(int packetID, IMessage msg)
    {
        var session = NetworkManager.Instance.FindSession(SessionID);
        session?.Send(packetID, msg.ToByteArray());
    }

    // ====================== 메시지 핸들러 ======================

    private void OnCOGetPlayerReq(COGetPlayerReq msg)
    {
        // TODO: player 데이터 조회 + OcGetPlayerAck 응답 (Send((int)EMessageID.OcGetPlayerAck, ack))
    }

    // 클라 필드 입장 요청. 입장 자격은 세션 상태와 권한을 아는 OutGame Player가 최종 판단한다.
    // 검증을 통과하면 OIEnterReq를 InGameService로 보내고 State를 Entering으로 둔다. InField 확정은 ack에서.
    private void OnCOEnterReq(COEnterReq msg)
    {
        // 로그인 직후 상태에서만 입장을 허용한다. Entering(왕복 중) / InField(이미 입장)의 중복 요청은 막는다.
        if (State != EPlayerState.LoggedIn)
        {
            Log.Warning("Enter rejected — invalid state SessionID={ID} State={State} StageID={Stage}",
                SessionID, State, msg.StageID);
            return;
        }

        State = EPlayerState.Entering;

        // StageID → RoomID는 현재 1:1(공유 필드, "맵=룸"). 인스턴스 던전이 필요해지면 여기서 인스턴스를 고른다.
        // 직접 참조 없이 OIEnterReq 패킷을 InGameService로 보낸다. 실제 입장 처리는 InGameService의 Prepare.
        var req = new OIEnterReq { RoomID = msg.StageID };
        InGameService.Instance.EnqueuePacket(SessionID, (int)EMessageID.OiEnterReq, req.ToByteArray());
    }

    // InGame 입장 확정 ack. InGame 룸이 Character 생성을 마친 뒤 돌려보낸다. 이 시점에 InField로 확정한다.
    // State 변경은 이 Player를 맡은 worker 하나만 하므로 안전하다.
    private void OnIOEnterAck(IOEnterAck msg)
    {
        if (State != EPlayerState.Entering)
        {
            Log.Warning("IOEnterAck ignored — not entering SessionID={ID} State={State} RoomID={RID}",
                SessionID, State, msg.RoomID);
            return;
        }

        State = EPlayerState.InField;
        Log.Debug("Enter confirmed SessionID={ID} RoomID={RID}", SessionID, msg.RoomID);
    }

    // 세션 종료(disconnect). NetDisconnect를 Work 단계에서 받아 DB 세션 종료 저장을 시작한다.
    // 실제 archive 제거와 InGame 캐릭터 제거는 DOExitGameSessionAck를 받은 Arrange(Eventor)가 한다.
    // 여기서는 DB 왕복만 시작하고 상태를 Disconnecting으로 바꾼다.
    private void OnNetDisconnect()
    {
        // 중복 방어. Session.OnDisconnected는 세션당 1회 보장이지만, 혹시 NetDisconnect가 두 번 들어와도
        // ODExitGameSessionReq를 다시 보내지 않도록 상태로 막는다.
        if (State == EPlayerState.Disconnecting)
            return;

        State = EPlayerState.Disconnecting;

        var req = new ODExitGameSessionReq
        {
            MessageID = EMessageID.OdExitGameSessionReq,
            AccountID = AccountID,
            PID       = PID,
        };
        DBService.Instance.Send(SessionID, (int)EMessageID.OdExitGameSessionReq, req);

        Log.Debug("ExitGameSession requested SessionID={ID} PID={PID}", SessionID, PID);
    }

    // 세션 종료 저장 완료 ack. 여기서는 로그만 남긴다.
    // archive 제거와 OIExitGameSessionReq(InGame 캐릭터 제거)는 같은 tick의 Arrange(OutGameLogicEventor.OnDOExitGameSessionAck)가 한다.
    private void OnDOExitGameSessionAck()
    {
        Log.Information("ExitGameSession ack SessionID={ID} PID={PID}", SessionID, PID);
    }
}
