namespace TH.Network
{
    // 클라 전용 enum 모음. 서버 THServerCommon/DefineEnum.cs 와 같은 방식이다.
    // 서버와 같이 쓰는 enum 은 여기가 아니라 enum.proto 에 둔다.

    // TcpConnection 이 끊긴 이유. 먼저 닫은 쪽의 이유 하나만 남는다.
    // 이 어셈블리는 UnityEngine 을 참조하지 않아 로그를 못 남기므로, OnDisconnected 를 받은 쪽이 이 값을 로그로 남긴다.
    public enum EDisconnectReason
    {
        None = 0,       // 아직 열려 있음
        LocalClose,     // 이쪽에서 Close() 를 불렀다
        RemoteClosed,   // 서버가 연결을 정상적으로 닫았다(수신 0바이트)
        SocketError,    // 수신 중 소켓 에러(RST, 네트워크 끊김 등)
        InvalidHeader,  // 길이가 잘못된 헤더를 받았다. 스트림이 깨진 것으로 본다
        SendFailed,     // 송신 중 소켓 에러
    }
}
