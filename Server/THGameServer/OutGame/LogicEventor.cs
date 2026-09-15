using Google.Protobuf;
using Serilog;
using TH.Common;
using TH.Common.Network;

namespace TH.Server.Logic;

// 한 tick의 Event → Prepare → Work → Arrange 단계 hook을 제공하는 베이스 클래스.
// 하위 클래스(OutGameLogicEventor)가 핸들러를 등록하고 Event()에 주기 작업을 구현한다.
// Prepare/Arrange는 tick 스레드에서만 호출되므로 내부 동기화가 없다. 상세는 docs/server-logic-architecture.md §2.
public abstract class LogicEventor
{
    private readonly Dictionary<int, HandlerEntry> _handlers = new();

    private readonly record struct HandlerEntry(
        ELogicEvent Phases,
        Action<long, ReadOnlyMemory<byte>, byte> Invoke);

    protected static bool IsPrepareEvent(byte flag) => (flag & (byte)ELogicEvent.Prepare) != 0;
    protected static bool IsArrangeEvent(byte flag) => (flag & (byte)ELogicEvent.Arrange) != 0;

    // 핸들러 등록. 패킷별 ParseFrom을 한 번만 수행하는 dispatch 델리게이트를 만들어 둔다.
    // 같은 패킷이 Prepare/Arrange 양쪽 phase에 등록되면 phase마다 한 번씩 파싱한다(단순화).
    protected void RegisterHandler<T>(int packetID, Action<long, T, byte> handler, ELogicEvent phases)
        where T : class, IMessage<T>, new()
    {
        var parser = new MessageParser<T>(() => new T());

        _handlers[packetID] = new HandlerEntry(phases, (sessionID, payload, flag) =>
        {
            T msg;
            try
            {
                msg = parser.ParseFrom(payload.Span);
            }
            catch (InvalidProtocolBufferException ex)
            {
                Log.Warning(ex, "Packet parse failed SessionID={ID} PacketID={PID}", sessionID, packetID);
                return;
            }

            handler(sessionID, msg, flag);
        });
    }

    // 주기 작업 hook. tick 시작 시 호출되며 하위 클래스가 시간 기반 작업을 구현한다.
    public abstract void Event(long tickMs);

    public virtual void Prepare(Dictionary<long, List<PacketMessage>> sessionPackets)
    {
        Dispatch(sessionPackets, ELogicEvent.Prepare);
    }

    // Work phase 진입점. Prepare와 Arrange 사이에서 호출된다.
    // 기본은 no-op. 세션 단위 병렬 처리가 필요한 하위 클래스(OutGameLogicEventor)가 override한다.
    public virtual void Work(long tickMs, Dictionary<long, List<PacketMessage>> sessionPackets)
    {
    }

    public virtual void Arrange(Dictionary<long, List<PacketMessage>> sessionPackets)
    {
        Dispatch(sessionPackets, ELogicEvent.Arrange);
    }

    private void Dispatch(Dictionary<long, List<PacketMessage>> sessionPackets, ELogicEvent phase)
    {
        if (_handlers.Count == 0) return;

        byte flag = (byte)phase;
        foreach (var (sessionID, packets) in sessionPackets)
        {
            foreach (var pkt in packets)
            {
                // Eventor 테이블에 없는 패킷은 Eventor 담당이 아니다. Work phase의 Player 핸들러가 처리하거나 무시된다.
                // 모든 패킷이 Prepare/Arrange 두 phase를 거치므로 여기서 "dropped"를 남기면 Player 패킷마다 로그가 2배로 쌓인다.
                // 미등록 패킷 로깅은 Player.Execute가 담당한다.
                if (!_handlers.TryGetValue(pkt.PacketID, out var entry))
                    continue;
                if ((entry.Phases & phase) == 0) continue;

                try
                {
                    entry.Invoke(sessionID, pkt.Payload, flag);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Eventor handler exception SessionID={ID} PacketID={PID} Phase={Phase}",
                        sessionID, pkt.PacketID, phase);
                }
            }
        }
    }

    // 세션으로 응답을 보내는 헬퍼.
    protected static void SendTo(long sessionID, int packetID, IMessage msg)
    {
        var session = NetworkManager.Instance.FindSession(sessionID);
        session?.Send(packetID, msg.ToByteArray());
    }
}
