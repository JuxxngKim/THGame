using System;
using System.Net.Sockets;
using Google.Protobuf;
using TH.Network;
using UnityEngine;

namespace TH.Client
{
    // 서버 연결 하나를 가진 MonoBehaviour. 씬이 바뀌어도 연결이 유지되도록 DontDestroyOnLoad 로 둔다.
    // 매 프레임 Update 에서 받은 패킷을 dispatch 하고, 오브젝트가 파괴될 때(Play 종료 포함) 연결을 닫는다.
    // 서버 NetworkManager 와 이름은 같지만, 이쪽은 서버로 가는 Session 하나만 관리한다.
    //
    // Title 씬에 배치한다. 다른 씬에서 바로 Play 하면 연결이 없다.
    // 다른 컴포넌트가 OnEnable 에서 Instance 를 쓰므로, 그보다 먼저 Awake 가 돌도록 실행 순서를 앞당긴다.
    [DefaultExecutionOrder(-1000)]
    public sealed class NetworkManager : MonoBehaviour
    {
        [SerializeField] private string _host = "127.0.0.1";
        [SerializeField] private int _port = 35501;

        private Session _session;

        public static NetworkManager Instance { get; private set; }

        // 패킷 핸들러 등록용. 씬 오브젝트는 OnEnable 에서 Register, OnDisable 에서 Unregister 한다.
        public PacketDispatcher Dispatcher { get; } = new PacketDispatcher();

        // 연결이 끊기면 메인 스레드에서 1회 불린다. 직접 Close 한 경우도 LocalClose 로 온다.
        // 에디터 Stop 때도 LocalClose 로 올 수 있으니, "끊기면 Title 로 복귀" 같은 처리는 LocalClose 를 무시해야 한다.
        public event Action<EDisconnectReason> Disconnected;

        public bool IsConnected => _session != null && _session.IsConnected;

        private void Awake()
        {
            // 씬에 배치한 오브젝트라 Title 씬에 다시 들어오면 하나가 더 생긴다. 먼저 있던 것을 남긴다.
            if (Instance != null)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);

            // 창이 포커스를 잃어도 Update 가 돌게 한다. 꺼져 있으면 빌드에서 포커스를 잃는 동안
            // dispatch 가 멈춰 받은 패킷이 큐에 쌓이고 화면도 갱신되지 않는다.
            // Player Settings 의 Run In Background 는 꺼져 있지만, 연결을 가진 이 컴포넌트가 실행 중에 켠다.
            Application.runInBackground = true;

            Dispatcher.OnUnhandled = packet => Debug.LogWarning($"Unhandled packet PacketID={packet.PacketID}");
            Dispatcher.OnError = (packet, ex) => Debug.LogError($"Packet handler failed PacketID={packet.PacketID}\n{ex}");
        }

        // 동기 접속. 성공하면 true.
        // 로컬 서버라 바로 끝나지만, 원격 서버면 끝날 때까지 화면이 멈춘다. 백그라운드 접속과 재접속은 로드맵 세션 10 에서 한다.
        public bool Connect()
        {
            if (_session != null)
                return IsConnected;

            var session = new Session { OnDisconnected = OnSessionDisconnected };
            try
            {
                session.Connect(_host, _port);
            }
            catch (SocketException ex)
            {
                Debug.LogWarning($"Connect failed {_host}:{_port} ({ex.SocketErrorCode})");
                return false;
            }
            catch (ArgumentException ex)
            {
                // Inspector 의 host·port 설정 오류(예: port 가 65535 초과). 잡지 않으면 화면이 "Connecting..." 에 멈춘다.
                Debug.LogError($"Connect failed {_host}:{_port} (invalid address: {ex.Message})");
                return false;
            }

            _session = session;
            return true;
        }

        // 연결이 없거나 보내는 중에 끊기면 false.
        public bool Send(int packetID, IMessage message) => _session != null && _session.Send(packetID, message);

        private void Update()
        {
            if (_session != null)
                Dispatcher.DispatchAll(_session);
        }

        private void OnSessionDisconnected(EDisconnectReason reason)
        {
            Debug.Log($"Disconnected: {reason}");
            Disconnected?.Invoke(reason);
        }

        private void OnDestroy()
        {
            if (Instance != this)
                return;

            Instance = null;
            // 빌드에서는 파괴된 뒤 Poll 을 부를 곳이 없으므로 끊김 알림이 오지 않는다.
            // 에디터 Stop 때는 NetworkEditorCleanup 이 먼저 닫고 Update 가 한 번 더 돌아서 Disconnected(LocalClose) 가 갈 수 있다.
            _session?.Close();
        }
    }
}
