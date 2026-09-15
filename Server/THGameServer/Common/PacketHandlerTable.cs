using Google.Protobuf;
using Serilog;
using TH.Server.Logic;

namespace TH.Server.Common;

// 패킷 dispatch 공통 테이블. Player / LoginSession / GameRoom이 각자 static 인스턴스를 가진다.
// static 생성자에서만 Register로 채우고 이후 읽기 전용이라 lock 없이 공유한다.
//
// 설계 이유:
//  - "MessageParser 생성 + ParseFrom + 호출" 패턴이 세 곳에 중복돼 있던 것을 한 곳으로 모았다.
//  - 핸들러에 PacketMessage도 함께 넘긴다. body가 없는 패킷(입장/퇴장)도 SessionID를 쓸 수 있어 Register<T> 하나로 통일된다.
//  - 핸들러 예외는 호출부(Execute/DrainInbox의 try/catch)가 처리한다. 한 패킷 실패가 다음 패킷을 막지 않게 하기 위해서다.
public sealed class PacketHandlerTable<TOwner>
{
    // packetID → (owner, packet) dispatch 델리게이트. Register 시점에 파싱 람다를 만들어 둔다.
    private readonly Dictionary<int, Action<TOwner, PacketMessage>> _handlers = new();

    // 핸들러 등록. 패킷별 ParseFrom을 한 번만 수행하는 dispatch 델리게이트를 만들어 둔다.
    // 파싱에 실패하면 Log.Warning 후 무시한다.
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

    // 핸들러가 있으면 호출하고 true, 없으면 false. 미등록 패킷 로깅은 호출부가 반환값을 보고 한다.
    public bool Dispatch(TOwner owner, in PacketMessage packet)
    {
        if (!_handlers.TryGetValue(packet.PacketID, out var invoke))
            return false;

        invoke(owner, packet);
        return true;
    }
}
