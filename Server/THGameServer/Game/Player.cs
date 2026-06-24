using Google.Protobuf;
using Serilog;
using Th;
using TH.Common;
using TH.Common.Network;
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

    // packetID → (player, payload) dispatch 델리게이트. static 생성자에서만 채우고 이후 읽기 전용.
    private static readonly Dictionary<int, Action<Player, ReadOnlyMemory<byte>>> Handlers = new();

    static Player()
    {
        // 로그인 패킷(COLoginReq/DOLoginAck)은 더 이상 Player 가 처리하지 않는다.
        // COLoginReq → LoginSession, DOLoginAck → OutGameLogicEventor(Prepare)에서 처리.
        Register<COGetPlayerReq>((int)EMessageID.CoGetPlayerReq, (p, m) => p.OnCOGetPlayerReq(m));

        // TODO(proto): 필드 진입 요청 패킷 추가 시 여기서 배선.
        //   Register<COEnterReq>((int)EMessageID.CoEnterReq, (p, m) => p.OnCOEnterReq(m));
        //   핸들러 본문에서 EnterField(roomID) 를 호출하면 InGame 진입이 트리거된다.
    }

    // 핸들러 등록 — 패킷별 ParseFrom 을 한 번만 수행하는 dispatch 델리게이트를 만들어 보관.
    private static void Register<T>(int packetID, Action<Player, T> handler)
        where T : class, IMessage<T>, new()
    {
        var parser = new MessageParser<T>(() => new T());

        Handlers[packetID] = (player, payload) =>
        {
            T msg;
            try
            {
                msg = parser.ParseFrom(payload.Span);
            }
            catch (InvalidProtocolBufferException ex)
            {
                Log.Warning(ex, "Player packet parse failed SessionID={ID} PacketID={PID}", player.SessionID, packetID);
                return;
            }

            handler(player, msg);
        };
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
            if (!Handlers.TryGetValue(pkt.PacketID, out var invoke))
            {
                Log.Debug("Unregistered player packet dropped SessionID={ID} PacketID={PID}", SessionID, pkt.PacketID);
                continue;
            }

            try
            {
                invoke(this, pkt.Payload);
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

    // ====================== InGame 진입 (크로스도메인) ======================

    // OutGame Player(메인, 영속)는 그대로 두고 InGame 룸에 필드 캐릭터 진입을 요청한다.
    // 직접 참조 없이 InGameService 명령 큐로만 전달 — 실제 진입 처리는 InGameService 의 Prepare phase.
    // (proto 추가 후 COEnterReq 핸들러에서 호출. 현재는 진입 배선 골격.)
    public void EnterField(RoomID roomID)
    {
        State = EPlayerState.InField;
        InGameService.Instance.EnqueueEnter(SessionID, roomID);
    }
}
