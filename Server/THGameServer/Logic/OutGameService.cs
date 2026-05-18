using System.Diagnostics;
using Serilog;
using TH.Common;
using TH.Common.Network;
using TH.Common.Time;

namespace TH.Server.Logic;

// C++ OutGameService 미러. tick 당 Event → Prepare → Arrange 의 phase 처리.
// Player abstraction 도입 시 Arrange 전에 worker phase(Player 단위 병렬 처리) 가 추가될 예정.
public sealed class OutGameService : Singleton<OutGameService>
{
    public const int TickIntervalMs = 300;

    private readonly OutGameLogicEventor _eventor = new();

    private Thread? _mainThread;
    private volatile bool _stopping;
    private long _nextUpdateTimeMs;

    private OutGameService() { }

    public OutGameLogicEventor Eventor => _eventor;

    public void Init()
    {
        _mainThread = new Thread(MainLoop) { IsBackground = true, Name = "LogicMain" };
        _mainThread.Start();

        Log.Information("OutGameService started (Tick={Tick}ms)", TickIntervalMs);
    }

    public void Shutdown()
    {
        _stopping = true;
        _mainThread?.Join();
        Log.Information("OutGameService shutdown");
    }

    private void MainLoop()
    {
        while (!_stopping)
        {
            try
            {
                long tickMs = TimeManager.Instance.UnixMillis();
                if (tickMs < _nextUpdateTimeMs)
                {
                    Thread.Sleep(1);
                    continue;
                }

                long tickStart = Stopwatch.GetTimestamp();

                _eventor.Event(tickMs);

                var raw = PacketQueue.Instance.Swap();
                var grouped = GroupBySession(raw);

                _eventor.Prepare(grouped);
                _eventor.Arrange(grouped);

                raw.Clear();

                _nextUpdateTimeMs = tickMs + TickIntervalMs;

                var elapsed = Stopwatch.GetElapsedTime(tickStart);
                if (elapsed.TotalMilliseconds > TickIntervalMs)
                    Log.Warning("Tick overrun: {Elapsed:F1}ms (target {Target}ms)", elapsed.TotalMilliseconds, TickIntervalMs);
                else
                    Thread.Sleep(1);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "OutGameService MainLoop exception");
            }
        }
    }

    private static Dictionary<long, List<PacketMessage>> GroupBySession(List<PacketMessage> raw)
    {
        var grouped = new Dictionary<long, List<PacketMessage>>();
        foreach (var p in raw)
        {
            // 끊긴 세션 패킷은 드롭. 단 NetDisconnect 합성 패킷은 세션 제거 후 들어오므로 항상 통과.
            if (p.PacketId != (int)Th.EMessageID.NetDisconnect &&
                !NetworkManager.Instance.IsSessionAlive(p.SessionId))
                continue;

            if (!grouped.TryGetValue(p.SessionId, out var list))
                grouped[p.SessionId] = list = new List<PacketMessage>();
            list.Add(p);
        }
        return grouped;
    }
}
