using Serilog;
using TH.Common;
using TH.Common.Game;

namespace TH.Server.Logic;

// 세션 단위 Player 보관자. tick 메인 스레드(Prepare/Arrange)에서만 호출되므로 lock 없음.
public sealed class PlayerArchive : Singleton<PlayerArchive>
{
    private readonly Dictionary<long, Player> _bySession = new();

    private PlayerArchive() { }

    public int Count => _bySession.Count;

    public bool TryRegister(Player player)
    {
        if (_bySession.ContainsKey(player.SessionId))
        {
            Log.Warning("PlayerArchive register skipped — already exists SessionId={Id}", player.SessionId);
            return false;
        }
        _bySession.Add(player.SessionId, player);
        return true;
    }

    public bool Remove(long sessionId) => _bySession.Remove(sessionId);

    public Player? Find(long sessionId)
        => _bySession.TryGetValue(sessionId, out var p) ? p : null;
}
