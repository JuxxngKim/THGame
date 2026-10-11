using UnityEditor;

namespace TH.Network.Editor
{
    // 에디터 전용 안전망. Play 를 멈추거나 스크립트를 다시 로드할 때 열려 있는 Session 을 모두 닫는다.
    // Play 를 멈출 때는 도메인 리로드가 없어서, 아무도 Close 하지 않으면 소켓과 수신 스레드가 Edit 모드까지 살아남는다.
    // 서버에는 idle timeout 이 없어서 그 세션도 에디터를 끌 때까지 남는다.
    // 기본 경로는 연결을 가진 MonoBehaviour 가 OnDestroy 에서 Close 하는 것이고, 이건 그걸 빠뜨렸을 때를 위한 것이다.
    [InitializeOnLoad]
    internal static class NetworkEditorCleanup
    {
        static NetworkEditorCleanup()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += Session.CloseAll;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingPlayMode)
                Session.CloseAll();
        }
    }
}
