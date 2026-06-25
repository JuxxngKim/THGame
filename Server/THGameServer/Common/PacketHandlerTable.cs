using Google.Protobuf;
using Serilog;
using TH.Server.Logic;

namespace TH.Server.Common;

// 패킷 dispatch 공통 테이블. Player / LoginSession / GameRoom 이 각자 static 인스턴스를 보유한다.
// static 생성자에서만 Register 로 채우고 이후 읽기 전용 → 무락 공유(기존 스레드 모델 유지).
//
// 설계 의도:
//  - 기존 세 곳에 중복돼 있던 "MessageParser 합성 + ParseFrom + invoke" 패턴을 한 곳으로 모은다.
//  - 핸들러 시그니처에 PacketMessage 를 함께 넘긴다 — body 가 필요 없는 패킷(진입/이탈)도 SessionID 등
//    메타데이터에 접근할 수 있어, body 유무와 무관하게 Register<T> 로 통일된다.
//  - 핸들러 예외는 호출부(각 Execute/DrainInbox 의 try/catch)가 책임진다 — 한 패킷 실패가 다음을
//    막지 않도록 패킷 단위로 감싸는 기존 동작을 보존하기 위함.
public sealed class PacketHandlerTable<TOwner>
{
    // packetID → (owner, packet) dispatch 델리게이트. Register 시점에 파싱 람다를 합성해 보관.
    private readonly Dictionary<int, Action<TOwner, PacketMessage>> _handlers = new();

    // 핸들러 등록 — 패킷별 ParseFrom 을 한 번만 수행하는 dispatch 델리게이트를 합성해 보관.
    // 파싱 실패 시 Log.Warning 후 무시(기존 동일 패턴).
    public void Register<T>(int packetID, Action<TOwner, PacketMessage, T> handler)
        where T : class, IMessage<T>, new()
    {
        var parser = new MessageParser<T>(() => new T());

        _handlers[packetID] = (owner, packet) =>
        {
            T msg;
            try
            {
                msg = parser.ParseFrom(packet.Payload);
            }
            catch (InvalidProtocolBufferException ex)
            {
                Log.Warning(ex, "Packet parse failed SessionID={ID} PacketID={PID}", packet.SessionID, packetID);
                return;
            }

            handler(owner, packet, msg);
        };
    }

    // 핸들러 조회 → 있으면 invoke 후 true, 없으면 false. 미등록 패킷 로깅은 호출부가 반환값으로 처리.
    public bool Dispatch(TOwner owner, in PacketMessage packet)
    {
        if (!_handlers.TryGetValue(packet.PacketID, out var invoke))
            return false;

        invoke(owner, packet);
        return true;
    }
}
