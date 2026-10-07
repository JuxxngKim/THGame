using Th;

namespace TH.Server.Common;

// 패킷 ID 대역 판정. 대역 정의는 enum.proto 의 EMessageID(*_BEGIN ~ *_END)를 따른다.
public static class PacketBand
{
    // 클라가 OutGame으로 보낼 수 있는 패킷인지 판정한다. NetAliveReq와 CO 클라 대역(10000~19999)만 true.
    // NetDisconnect(서버가 만드는 패킷)와 OD/DO·OI/IO(서버 내부 패킷)는 false다.
    public static bool IsClientToOutGame(int packetID)
        => packetID == (int)EMessageID.NetAliveReq
        || IsInBand(packetID, EMessageID.CoClientOutgameBegin, EMessageID.CoClientOutgameEnd);

    // 클라가 InGame으로 보낼 수 있는 패킷인지 판정한다. CI 클라 대역(70000~79999)만 true.
    // IC 대역(60000~69999)은 서버가 클라로 보내는 방향이라 false다.
    public static bool IsClientToInGame(int packetID)
        => IsInBand(packetID, EMessageID.CiClientIngameBegin, EMessageID.CiClientIngameEnd);

    // BEGIN/END는 대역 경계 표시용이고 실제 패킷이 아니므로 범위에서 뺀다.
    private static bool IsInBand(int packetID, EMessageID begin, EMessageID end)
        => packetID > (int)begin && packetID < (int)end;
}
