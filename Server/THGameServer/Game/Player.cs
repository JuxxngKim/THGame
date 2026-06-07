using Google.Protobuf;
using Serilog;
using Th;
using TH.Common;
using TH.Common.Network;
using TH.Common.Time;
using TH.Server.Logic;

namespace TH.Server.Game;

// 세션-스코프 Player 엔티티. tick 메인 스레드 또는 worker 스레드 1개가 단독 접근하므로 동기화 멤버 없음.
// 패킷 핸들러 테이블은 static 공유(전 Player 공통), 처리 본문은 인스턴스 메서드.
public sealed class Player
{
    public long SessionId { get; }
    public long AccountId { get; set; }
    public string Pid { get; set; } = string.Empty;
    public EPlayerState State { get; set; }
    public byte LoadingFlags { get; set; }
    public long LastPacketUnixMs { get; set; }
    public long CreatedUnixMs { get; }

    public Player(long sessionId)
    {
        SessionId = sessionId;
        long now = TimeManager.Instance.UnixMillis();
        CreatedUnixMs = now;
        LastPacketUnixMs = now;
        State = EPlayerState.Connecting;
    }

    // ====================== 패킷 핸들러 테이블 (static 공유) ======================

    // packetId → (player, payload) dispatch 델리게이트. static 생성자에서만 채우고 이후 읽기 전용.
    private static readonly Dictionary<int, Action<Player, ReadOnlyMemory<byte>>> Handlers = new();

    static Player()
    {
        Register<CAGetPlayerReq>((int)EMessageID.CaGetPlayerReq, (p, m) => p.OnCAGetPlayerReq(m));
    }

    // 핸들러 등록 — 패킷별 ParseFrom 을 한 번만 수행하는 dispatch 델리게이트를 만들어 보관.
    private static void Register<T>(int packetId, Action<Player, T> handler)
        where T : class, IMessage<T>, new()
    {
        var parser = new MessageParser<T>(() => new T());

        Handlers[packetId] = (player, payload) =>
        {
            T msg;
            try
            {
                msg = parser.ParseFrom(payload.Span);
            }
            catch (InvalidProtocolBufferException ex)
            {
                Log.Warning(ex, "Player packet parse failed SessionId={Id} PacketId={Pid}", player.SessionId, packetId);
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
            if (!Handlers.TryGetValue(pkt.PacketId, out var invoke))
            {
                Log.Debug("Unregistered player packet dropped SessionId={Id} PacketId={Pid}", SessionId, pkt.PacketId);
                continue;
            }

            try
            {
                invoke(this, pkt.Payload);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Player handler exception SessionId={Id} PacketId={Pid}", SessionId, pkt.PacketId);
            }
        }

        // 2) tick 단위 Player 로직 (버프 만료 / 타이머 등) — 후속 구현.
        _ = tickMs;
    }

    // 자기 세션으로의 응답 송신 헬퍼. Session.Send 는 스레드 안전 — worker 에서 호출해도 안전.
    private void Send(int packetId, IMessage msg)
    {
        var session = NetworkManager.Instance.FindSession(SessionId);
        session?.Send(packetId, msg.ToByteArray());
    }

    // ====================== 메시지 핸들러 ======================

    private void OnCAGetPlayerReq(CAGetPlayerReq msg)
    {
        // TODO: player 데이터 조회 + AcGetPlayerAck 응답 (Send((int)EMessageID.AcGetPlayerAck, ack))
    }
}
