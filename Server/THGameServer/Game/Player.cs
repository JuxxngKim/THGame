using Google.Protobuf;
using Serilog;
using Th;
using TH.Common;
using TH.Common.Network;
using TH.Server.Common;
using TH.Server.Logic;

namespace TH.Server.Game;

// 세션-스코프 Player 엔티티. tick 메인 스레드 또는 worker 스레드 1개가 단독 접근하므로 동기화 멤버 없음.
// 패킷 핸들러 테이블은 static 공유(전 Player 공통), 처리 본문은 인스턴스 메서드.
public sealed class Player : ISessionWorker
{
    public long SessionID { get; }
    public long AccountID { get; set; }
    public string PID { get; set; } = string.Empty;
    public EPlayerState State { get; set; }

    // Player 는 DB 인증 성공(DOLoginAck) 후에만 생성된다 — 항상 로그인 완료 상태로 시작.
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
        // 로그인 패킷(COLoginReq/DOLoginAck)은 더 이상 Player 가 처리하지 않는다.
        // COLoginReq → LoginSession, DOLoginAck → OutGameLogicEventor(Prepare)에서 처리.
        // 핸들러 본문은 msg 만 쓰므로 pkt 는 무시한다.
        Table.Register<COGetPlayerReq>((int)EMessageID.CoGetPlayerReq, (p, pkt, m) => p.OnCOGetPlayerReq(m));
        Table.Register<COEnterReq>((int)EMessageID.CoEnterReq, (p, pkt, m) => p.OnCOEnterReq(m));
        Table.Register<IOEnterAck>((int)EMessageID.IoEnterAck, (p, pkt, m) => p.OnIOEnterAck(m));
    }

    // ====================== worker phase 진입점 ======================

    // 한 tick 실행 — worker 스레드 1개가 이 Player 를 단독으로 담당한다.
    // 그 tick 에 도착한 packets 를 도착 순서대로 처리한 뒤 tick 단위 로직을 수행.
    // 규약: 자기 자신과 자기 세션(Send) 외의 전역 / 타 Player 상태를 변경하지 말 것.
    //       교차 변경이 필요하면 Arrange phase(단일 tick 스레드)로 미룬다.
    public void Execute(long tickMs, List<PacketMessage> packets)
    {
        // 1) 입력 패킷 처리 (도착 순서 보존). 한 패킷의 실패가 다음 패킷을 막지 않도록 패킷마다 try/catch.
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

        // 2) tick 단위 Player 로직 (버프 만료 / 타이머 등) — 후속 구현.
        _ = tickMs;
    }

    // 자기 세션으로의 응답 송신 헬퍼. Session.Send 는 스레드 안전 — worker 에서 호출해도 안전.
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

    // 클라 필드 진입 요청 — 입장 자격 검증의 권위 지점(OutGame Player 가 세션 상태/권한을 안다).
    // 검증 통과 시 OIEnterReq 를 InGameService 로 쏘고 State 를 Entering 으로 둔다(InField 확정은 ack 에서).
    private void OnCOEnterReq(COEnterReq msg)
    {
        // 로그인 직후 상태에서만 입장 허용 — Entering(왕복 중) / InField(이미 진입) 의 중복 요청 차단.
        if (State != EPlayerState.LoggedIn)
        {
            Log.Warning("Enter rejected — invalid state SessionID={ID} State={State} StageID={Stage}",
                SessionID, State, msg.StageID);
            return;
        }

        State = EPlayerState.Entering;

        // StageID → RoomID 는 현재 1:1(공유 필드, "맵=룸"). 인스턴싱이 필요해지면 여기서 인스턴스 선택.
        // 직접 참조 없이 OIEnterReq 패킷을 InGameService 로 쏜다 — 실제 진입 처리는 InGameService 의 Prepare.
        var req = new OIEnterReq { RoomID = msg.StageID };
        InGameService.Instance.EnqueuePacket(SessionID, (int)EMessageID.OiEnterReq, req.ToByteArray());
    }

    // InGame 입장 확정 ack — InGame 룸이 Character 생성을 마친 뒤 되돌려보낸다. 이 시점에 InField 확정.
    // (State mutate 는 OutGame tick 스레드 = 이 Player 의 worker 단독이라 안전.)
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
}
