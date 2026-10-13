using System;
using System.Collections.Generic;
using Google.Protobuf;

namespace TH.Network
{
    // 수신 패킷을 packetID 별 핸들러로 나눠 준다. 메인 스레드에서만 쓴다(핸들러가 Unity API 를 부를 수 있으므로).
    // 서버 PacketDispatcher 와 같은 방식: 등록할 때 파싱 델리게이트를 만들어 두고, dispatch 때는 찾아서 호출만 한다.
    public sealed class PacketDispatcher
    {
        // 등록 하나. Unregister 가 "등록한 그 핸들러"인지 비교하려고 원래 핸들러도 같이 둔다.
        private readonly struct Entry
        {
            public readonly Delegate Handler;
            public readonly Action<byte[]> Invoke;

            public Entry(Delegate handler, Action<byte[]> invoke)
            {
                Handler = handler;
                Invoke = invoke;
            }
        }

        private readonly Dictionary<int, Entry> _handlers = new Dictionary<int, Entry>();
        private readonly Action<PacketMessage> _dispatch;

        // 핸들러가 없는 패킷. 비워 두면 조용히 버린다.
        // 이 어셈블리는 UnityEngine 을 참조하지 않으므로 로그는 연결한 쪽에서 남긴다.
        public Action<PacketMessage> OnUnhandled;

        // 파싱 실패나 핸들러 예외. 설정하면 예외를 여기로 넘기고 다음 패킷을 계속 처리한다.
        // 비워 두면 예외를 그대로 던진다. 에러가 조용히 사라지지 않게 하기 위해서다.
        public Action<PacketMessage, Exception> OnError;

        public PacketDispatcher()
        {
            // DispatchAll 마다 델리게이트를 새로 만들지 않도록 한 번만 만들어 둔다.
            _dispatch = Dispatch;
        }

        // 핸들러 등록. packetID 는 메시지 타입에서 꺼낸다(서버 PacketDispatcher 와 같은 방식).
        // 같은 packetID 를 두 번 등록하면 예외. 덮어쓰면 먼저 등록한 핸들러가 조용히 사라지기 때문이다.
        public void Register<T>(Action<T> handler) where T : class, IMessage<T>, new()
        {
            int packetID = ResolvePacketID<T>();
            var parser = new MessageParser<T>(() => new T());
            var entry = new Entry(handler, payload => handler(parser.ParseFrom(payload)));
            if (!_handlers.TryAdd(packetID, entry))
                throw new InvalidOperationException($"PacketID {packetID} is already registered ({typeof(T).Name})");
        }

        // 등록을 해제한다. 씬 오브젝트처럼 수명이 짧은 주인은 OnEnable 에서 Register, OnDisable 에서 Unregister 한다.
        // 해제하지 않으면 그 오브젝트가 다시 생길 때(예: Title 씬 재진입) 중복 등록 예외가 나고,
        // 파괴된 오브젝트의 핸들러가 남아 패킷이 오면 MissingReferenceException 이 난다.
        // 등록한 그 핸들러일 때만 지운다. 남의 핸들러를 지우지 않기 위해서다
        // (예: Register 가 중복으로 실패한 오브젝트가 OnDisable 에서 Unregister 를 불러도 먼저 등록한 쪽은 남는다).
        // 메서드 그룹(OnLoginAck)은 같은 인스턴스·같은 메서드면 서로 같은 델리게이트로 비교된다. 지웠으면 true.
        public bool Unregister<T>(Action<T> handler) where T : class, IMessage<T>, new()
        {
            int packetID = ResolvePacketID<T>();
            if (!_handlers.TryGetValue(packetID, out var entry) || !entry.Handler.Equals(handler))
                return false;

            _handlers.Remove(packetID);
            return true;
        }

        public void Dispatch(PacketMessage packet)
        {
            if (!_handlers.TryGetValue(packet.PacketID, out var entry))
            {
                OnUnhandled?.Invoke(packet);
                return;
            }

            try
            {
                entry.Invoke(packet.Payload);
            }
            catch (Exception ex) when (OnError != null)
            {
                OnError(packet, ex);
            }
        }

        // 연결의 수신 큐가 빌 때까지 꺼내 처리한다. Update 에서 매 프레임 한 번 부른다. 처리한 개수를 돌려준다.
        // 연결이 끊겼으면 남은 패킷을 모두 처리한 뒤 session.OnDisconnected 가 1회 불린다(직접 Close 한 경우는 남은 패킷 없이).
        public int DispatchAll(Session session) => session.Poll(_dispatch);

        // proto 의 패킷 메시지에는 모두 `optional EMessageID MessageID = 99 [default = XXX];` 가 있고, 그 default 가 packetID 다.
        // 서버 PacketDispatcher.ResolvePacketID 와 같은 코드다. 고치면 양쪽을 같이 고친다.
        // 생성된 속성 값이 아니라 proto 에 적힌 default 를 descriptor 에서 직접 읽는다.
        // [default] 를 빠뜨리면 속성 값은 enum 첫 값(NET_DISCONNECT)이 되어 다른 패킷과 조용히 겹치기 때문이다.
        // IL2CPP 빌드에서는 아직 확인하지 않았다. Google.Protobuf 3.34.1 은 descriptor 를 만들 때
        // 필드 accessor(reflection)도 같이 만들기 때문에, 첫 IL2CPP 빌드에서 Register 가 도는지 확인해야 한다.
        private static int ResolvePacketID<T>() where T : class, IMessage<T>, new()
        {
            var descriptor = new T().Descriptor;
            var field = descriptor.FindFieldByName("MessageID")
                ?? throw new InvalidOperationException($"{descriptor.Name} has no MessageID field");

            var proto = field.ToProto();
            if (!proto.HasDefaultValue)
                throw new InvalidOperationException($"{descriptor.Name}.MessageID has no [default]");

            var value = field.EnumType.FindValueByName(proto.DefaultValue)
                ?? throw new InvalidOperationException($"{descriptor.Name}.MessageID default '{proto.DefaultValue}' is not an EMessageID");

            // 이름 규칙: enum 이름에서 '_' 를 빼면 메시지 이름과 대소문자만 다르다(CO_ENTER_REQ ↔ COEnterReq).
            // 메시지를 복붙하고 default 를 안 고친 실수를 여기서 잡는다.
            if (!string.Equals(value.Name.Replace("_", ""), descriptor.Name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{descriptor.Name}.MessageID default '{value.Name}' does not match the message name");

            return value.Number;
        }
    }
}
