namespace TH.Server.Logic;

// InGame 패킷 대역 정의 + 분류. proto 무수정 단계이므로 EMessageID(generated)가 아닌 코드 상수로 둔다.
// 대역: 50000~59999 (OutGame 10000~19999 / DB 20100~49999 위의 빈 영역). proto 에 InGame 패킷을
// 추가할 때도 이 대역 안에서 messageId 를 할당하면 라우팅은 그대로 동작한다.
public static class InGameMessage
{
    public const int Begin = 50000;
    public const int End = 59999;

    // Session.OnRecvPacket 분배 기준 — 대역 범위가 아니라 명시적 상수 비교로 OutGame/InGame 을 가른다.
    public static bool IsInGame(int packetId) => packetId >= Begin && packetId <= End;
}
