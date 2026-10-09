using System;
using System.Collections.Generic;
using Google.Protobuf;

namespace TH.Network
{
    // 수신 패킷을 packetID 별 핸들러로 나눠 준다. 메인 스레드에서만 쓴다(핸들러가 Unity API 를 부를 수 있으므로).
    // 서버 PacketHandlerTable 과 같은 방식: 등록할 때 파싱 델리게이트를 만들어 두고, dispatch 때는 찾아서 호출만 한다.
    public sealed class PacketDispatcher
    {
        private readonly Dictionary<int, Action<byte[]>> _handlers = new Dictionary<int, Action<byte[]>>();

        // 핸들러가 없는 패킷. 비워 두면 조용히 버린다.
        // 이 어셈블리는 UnityEngine 을 참조하지 않으므로 로그는 연결한 쪽에서 남긴다.
        public Action<ReceivedPacket> OnUnhandled;

        // 파싱 실패나 핸들러 예외. 설정하면 예외를 여기로 넘기고 다음 패킷을 계속 처리한다.
        // 비워 두면 예외를 그대로 던진다. 에러가 조용히 사라지지 않게 하기 위해서다.
        public Action<ReceivedPacket, Exception> OnError;

        public void Register<T>(int packetID, Action<T> handler) where T : class, IMessage<T>, new()
        {
            var parser = new MessageParser<T>(() => new T());
            _handlers[packetID] = payload => handler(parser.ParseFrom(payload));
        }

        public void Dispatch(ReceivedPacket packet)
        {
            if (!_handlers.TryGetValue(packet.PacketID, out var handle))
            {
                OnUnhandled?.Invoke(packet);
                return;
            }

            try
            {
                handle(packet.Payload);
            }
            catch (Exception ex) when (OnError != null)
            {
                OnError(packet, ex);
            }
        }

        // 연결의 수신 큐가 빌 때까지 꺼내 처리한다. Update 에서 매 프레임 한 번 부른다. 처리한 개수를 돌려준다.
        public int DispatchAll(TcpConnection connection)
        {
            int count = 0;
            while (connection.TryDequeue(out var packet))
            {
                Dispatch(packet);
                count++;
            }
            return count;
        }
    }
}
