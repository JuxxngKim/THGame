using Serilog;
using TH.Server.Game;

namespace TH.Server.Logic;

// 세션 단위 Player 보관자. tick 메인 스레드(Prepare/Arrange)에서만 호출되므로 lock 없음.
// OutGameLogicEventor 가 소유(composition)하는 도메인 자료구조 — 전역 상태 아님.
public sealed class PlayerArchive
{
    private readonly Dictionary<long, Player> _bySession = new();

    public int Count => _bySession.Count;

    // worker phase 전체 순회용. Work 동안 archive 는 불변(등록/제거는 Prepare 에서만)이므로 열거 안전.
    public Dictionary<long, Player>.ValueCollection Players => _bySession.Values;

    public bool TryRegister(Player player)
    {
        if (_bySession.ContainsKey(player.SessionID))
        {
            Log.Warning("PlayerArchive register skipped — already exists SessionID={ID}", player.SessionID);
            return false;
        }
        _bySession.Add(player.SessionID, player);
        return true;
    }

    public bool Remove(long sessionID) => _bySession.Remove(sessionID);

    public Player? Find(long sessionID)
        => _bySession.TryGetValue(sessionID, out var p) ? p : null;
}
