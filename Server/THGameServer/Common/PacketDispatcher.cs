using Google.Protobuf;
using Serilog;
using TH.Server.Logic;

namespace TH.Server.Common;

// 패킷 dispatch 공통 테이블. Player / LoginSession / GameRoom이 각자 static 인스턴스를 가진다.
// static 생성자에서만 Register로 채우고 이후 읽기 전용이라 lock 없이 공유한다.
// 여기에 상태(큐, 카운터 등)를 넣지 않는다. 넣으면 여러 worker 스레드가 lock 없이 공유한다는 전제가 깨진다.
//
// 설계 이유:
//  - "MessageParser 생성 + ParseFrom + 호출" 패턴이 세 곳에 중복돼 있던 것을 한 곳으로 모았다.
//  - packetID는 메시지 타입에서 꺼낸다(proto에 적힌 MessageID 필드의 default). ResolvePacketID가 default 누락,
//    메시지 이름과 맞지 않는 default(복붙 실수), 같은 packetID 중복 등록을 예외로 막는다.
//    GameServerApp.InitPacketHandlers(Start에서 호출)가 static 생성자를 미리 돌리므로 이 예외는 서버 시작 때 난다.
//    송신(Send)이나 LogicEventor 등록처럼 이 Dispatcher 밖에서는 여전히 packetID를 직접 쓴다.
//  - PacketMessage(SessionID 등)가 필요한 핸들러는 3인자 Register를, 나머지는 2인자 Register를 쓴다.
//    GameRoom처럼 주인 하나가 여러 세션의 패킷을 받는 경우가 3인자다.
//  - 핸들러 예외는 호출부(Execute/DrainInbox의 try/catch)가 처리한다. 한 패킷 실패가 다음 패킷을 막지 않게 하기 위해서다.
public sealed class PacketDispatcher<TOwner>
{
    // packetID → (owner, packet) dispatch 델리게이트. Register 시점에 파싱 람다를 만들어 둔다.
    private readonly Dictionary<int, Action<TOwner, PacketMessage>> _handlers = new();

    // 핸들러 등록. 핸들러가 주인과 메시지만 쓸 때.
    public void Register<T>(Action<TOwner, T> handler)
        where T : class, IMessage<T>, new()
        => Register<T>((owner, packet, msg) => handler(owner, msg));

    // 핸들러 등록. 패킷별 ParseFrom을 한 번만 수행하는 dispatch 델리게이트를 만들어 둔다.
    // 파싱에 실패하면 Log.Warning 후 무시한다.
    public void Register<T>(Action<TOwner, PacketMessage, T> handler)
        where T : class, IMessage<T>, new()
    {
        int packetID = ResolvePacketID<T>();
        var parser = new MessageParser<T>(() => new T());

        // 덮어쓰면 먼저 등록한 핸들러가 조용히 사라진다(예: OnNetDisconnect가 사라지면 Player가 영구히 남는다).
        bool added = _handlers.TryAdd(packetID, (owner, packet) =>
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
        });

        if (!added)
            throw new InvalidOperationException($"PacketID {packetID} is already registered ({typeof(T).Name})");
    }

    // 핸들러가 있으면 호출하고 true, 없으면 false. 미등록 패킷 로깅은 호출부가 반환값을 보고 한다.
    public bool Dispatch(TOwner owner, in PacketMessage packet)
    {
        if (!_handlers.TryGetValue(packet.PacketID, out var invoke))
            return false;

        invoke(owner, packet);
        return true;
    }

    // proto의 패킷 메시지에는 모두 `optional EMessageID MessageID = 99 [default = XXX];` 가 있고, 그 default가 packetID다.
    // 클라 PacketDispatcher.ResolvePacketID와 같은 코드다. 고치면 양쪽을 같이 고친다.
    // 생성된 속성 값이 아니라 proto에 적힌 default를 descriptor에서 직접 읽는다.
    // [default]를 빠뜨리면 속성 값은 enum 첫 값(NET_DISCONNECT)이 되어 다른 패킷과 조용히 겹치기 때문이다.
    // 등록할 때 한 번만 읽으므로 비용은 서버 시작 때뿐이다.
    private static int ResolvePacketID<T>()
        where T : class, IMessage<T>, new()
    {
        var descriptor = new T().Descriptor;
        var field = descriptor.FindFieldByName("MessageID")
            ?? throw new InvalidOperationException($"{descriptor.Name} has no MessageID field");

        var proto = field.ToProto();
        if (!proto.HasDefaultValue)
            throw new InvalidOperationException($"{descriptor.Name}.MessageID has no [default]");

        var value = field.EnumType.FindValueByName(proto.DefaultValue)
            ?? throw new InvalidOperationException($"{descriptor.Name}.MessageID default '{proto.DefaultValue}' is not an EMessageID");

        // 이름 규칙: enum 이름에서 '_'를 빼면 메시지 이름과 대소문자만 다르다(CO_ENTER_REQ ↔ COEnterReq).
        // 메시지를 복붙하고 default를 안 고친 실수를 여기서 잡는다.
        if (!string.Equals(value.Name.Replace("_", ""), descriptor.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{descriptor.Name}.MessageID default '{value.Name}' does not match the message name");

        return value.Number;
    }
}
