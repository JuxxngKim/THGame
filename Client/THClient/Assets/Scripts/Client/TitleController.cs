using TH.Network;
using Th;
using TMPro;
using UnityEngine;

namespace TH.Client
{
    // Title 씬. 시작하면 서버에 접속해 로그인하고, 결과(AccountID 또는 끊긴 이유)를 화면에 보여 준다.
    // 화면 문구는 영어로 쓴다. TMP 기본 폰트에 한글 글리프가 없기 때문이다.
    public sealed class TitleController : MonoBehaviour
    {
        [SerializeField] private TMP_Text _statusText;

        // 서버 DB 가 아직 stub 이라 PID·AuthToken 은 검사하지 않는다.
        [SerializeField] private string _pid = "unity_pid";

        private void OnEnable()
        {
            // 같은 패킷을 두 번 Register 하면 예외다. Title 에 다시 들어올 때를 위해 OnDisable 에서 꼭 해제한다.
            NetworkManager.Instance.Dispatcher.Register<OCLoginAck>(OnLoginAck);
            NetworkManager.Instance.Disconnected += OnDisconnected;
        }

        private void OnDisable()
        {
            // 앱 종료 때는 NetworkManager 가 먼저 파괴될 수 있다.
            var manager = NetworkManager.Instance;
            if (manager == null)
                return;

            manager.Dispatcher.Unregister<OCLoginAck>(OnLoginAck);
            manager.Disconnected -= OnDisconnected;
        }

        private void Start()
        {
            SetStatus("Connecting...");
            if (!NetworkManager.Instance.Connect())
            {
                SetStatus("Connect failed");
                return;
            }

            var req = new COLoginReq
            {
                CurrentVersion = (int)EProtocolVersion.ReProtocolVersion,
                PID = _pid,
                AuthToken = "dummy_token",
            };
            if (!NetworkManager.Instance.Send((int)EMessageID.CoLoginReq, req))
            {
                SetStatus("Send failed");
                return;
            }

            SetStatus("Logging in...");
        }

        private void OnLoginAck(OCLoginAck ack)
        {
            Debug.Log($"Login ok AccountID={ack.AccountID} Name={ack.AccountName}");
            SetStatus($"AccountID: {ack.AccountID}");
        }

        private void OnDisconnected(EDisconnectReason reason)
        {
            SetStatus($"Disconnected: {reason}");
        }

        private void SetStatus(string text)
        {
            _statusText.text = text;
        }
    }
}
