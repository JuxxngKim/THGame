using TH.Common;
using TH.Common.Time;

namespace TH.Server.Game;

// 세션-스코프 Player 엔티티. tick 메인 스레드에서만 접근되므로 동기화 멤버 없음.
public sealed class Player
{
    public long SessionId { get; }
    public long AccountId { get; set; }
    public string Pid { get; set; } = string.Empty;
    public EPlayerState State { get; set; }
    public byte LoadingFlags { get; set; }
    public long LastPacketUnixMs { get; set; }
    public long CreatedUnixMs { get; }

    public Player(long sessionId)
    {
        SessionId = sessionId;
        long now = TimeManager.Instance.UnixMillis();
        CreatedUnixMs = now;
        LastPacketUnixMs = now;
        State = EPlayerState.Connecting;
    }
}
