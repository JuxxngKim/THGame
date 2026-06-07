using Google.Protobuf;
using Serilog;
using TH.Common.Network;

namespace TH.Server.Logic;

// 한 tick 안에서 Event → Prepare → (Worker) → Arrange 의 단계 진행 hook 을 제공한다.
// Subclass(예: OutGameLogicEventor)가 핸들러를 등록하고 Event() 안에 주기 작업을 구현한다.
// Prepare / Arrange 는 tick 스레드에서만 호출되므로 내부 동기화 없음.
public abstract class LogicEventor
{
    private readonly Dictionary<int, HandlerEntry> _handlers = new();

    private readonly record struct HandlerEntry(
        ELogicEvent Phases,
        Action<long, ReadOnlyMemory<byte>, byte> Invoke);

    protected static bool IsPrepareEvent(byte flag) => (flag & (byte)ELogicEvent.Prepare) != 0;
    protected static bool IsArrangeEvent(byte flag) => (flag & (byte)ELogicEvent.Arrange) != 0;

    // 핸들러 등록 — 패킷별 ParseFrom 을 한 번만 수행하는 dispatch 델리게이트를 만들어 보관.
    // 같은 패킷이 Prepare/Arrange 양쪽 phase 에 흘러갈 경우 phase 마다 1회씩 파싱 (단순화).
    protected void RegisterHandler<T>(int packetId, Action<long, T, byte> handler, ELogicEvent phases)
        where T : class, IMessage<T>, new()
    {
        var parser = new MessageParser<T>(() => new T());

        _handlers[packetId] = new HandlerEntry(phases, (sessionId, payload, flag) =>
        {
            T msg;
            try
            {
                msg = parser.ParseFrom(payload.Span);
            }
            catch (InvalidProtocolBufferException ex)
            {
                Log.Warning(ex, "Packet parse failed SessionId={Id} PacketId={Pid}", sessionId, packetId);
                return;
            }

            handler(sessionId, msg, flag);
        });
    }

    // 주기 작업 hook — tick 시작 시 호출. subclass 가 시간 기반 동기화 작업을 구현.
    public abstract void Event(long tickMs);

    public virtual void Prepare(Dictionary<long, List<PacketMessage>> sessionPackets)
    {
        Dispatch(sessionPackets, ELogicEvent.Prepare);
    }

    // worker phase 진입점 — Prepare 와 Arrange 사이에서 호출.
    // 기본은 no-op. Player 단위 병렬 처리가 필요한 subclass(OutGameLogicEventor)가 override.
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
        foreach (var (sessionId, packets) in sessionPackets)
        {
            foreach (var pkt in packets)
            {
                if (!_handlers.TryGetValue(pkt.PacketId, out var entry))
                {
                    Log.Debug("Unregistered packet dropped SessionId={Id} PacketId={Pid}", sessionId, pkt.PacketId);
                    continue;
                }
                if ((entry.Phases & phase) == 0) continue;

                try
                {
                    entry.Invoke(sessionId, pkt.Payload, flag);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Eventor handler exception SessionId={Id} PacketId={Pid} Phase={Phase}",
                        sessionId, pkt.PacketId, phase);
                }
            }
        }
    }

    // 응답 송신 헬퍼. C++ OutGameLogicEventor::SendTo 동등.
    protected static void SendTo(long sessionId, int packetId, IMessage msg)
    {
        var session = NetworkManager.Instance.FindSession(sessionId);
        session?.Send(packetId, msg.ToByteArray());
    }
}
