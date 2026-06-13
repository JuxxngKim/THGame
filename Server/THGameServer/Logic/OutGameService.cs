using System.Diagnostics;
using Serilog;
using TH.Common;
using TH.Common.Network;
using TH.Common.Time;

namespace TH.Server.Logic;

// C++ OutGameService 미러. tick 당 Event → Prepare → Work → Arrange 의 phase 처리.
// Work 는 Player 단위 병렬 처리(worker phase) — Prepare 와 Arrange 사이에서 전체 Player 를 분산 처리.
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
        // 다음 tick 예정 시각의 초기 anchor. 이후 매 tick 절대 시각 기반(+=)으로 진전시킨다.
        _nextUpdateTimeMs = TimeManager.Instance.UnixMillis();

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

                // 다음 예정 시각을 phase 처리 "전에" 절대 시각 기반으로 진전시킨다.
                // - drift 방지: anchor 를 "깨어난 실제 시각"이 아니라 직전 예정 시각에 누적(Sleep 분해능 누적 제거).
                // - catch-up: 한참 밀렸으면 burst 로 몰지 않고 놓친 tick 을 건너뛴다.
                // - 핫루프 방지: phase 에서 예외가 나도 예정 시각이 이미 진전돼 다음 루프가 즉시 재처리하지 않는다.
                do
                {
                    _nextUpdateTimeMs += TickIntervalMs;
                } while (_nextUpdateTimeMs <= tickMs);

                long tickStart = Stopwatch.GetTimestamp();
                ProcessTick(tickMs);

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

    // 한 tick 본체 — Event → Prepare → Work → Arrange. 타이밍/예외 격리는 MainLoop 책임.
    private void ProcessTick(long tickMs)
    {
        _eventor.Event(tickMs);

        var raw = PacketQueue.Instance.Swap();
        var grouped = GroupBySession(raw);

        _eventor.Prepare(grouped);
        _eventor.Work(tickMs, grouped);
        _eventor.Arrange(grouped);

        raw.Clear();
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
