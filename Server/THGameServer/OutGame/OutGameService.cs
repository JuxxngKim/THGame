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
    private readonly PacketQueue _packetQueue = new();

    private Thread? _mainThread;
    private volatile bool _stopping;
    private long _nextUpdateTimeMs;

    private OutGameService() { }

    public OutGameLogicEventor Eventor => _eventor;

    // 외부(IO 스레드 / Data 계층)에서 tick 입력 큐로 패킷을 적재하는 유일한 진입점.
    // 내부 큐 구현(PacketQueue)을 캡슐화한다. Enqueue 는 lock 보호되어 멀티스레드 안전.
    public void EnqueuePacket(long sessionID, int packetID, byte[] payload)
        => _packetQueue.Enqueue(sessionID, packetID, payload);

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
        // 다음 tick 예정 시각의 초기 anchor. 이후 매 tick monotonic 시각 기반(+=)으로 진전시킨다.
        _nextUpdateTimeMs = TimeManager.Instance.TickMillis();

        while (!_stopping)
        {
            try
            {
                long tickMs = TimeManager.Instance.TickMillis();
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

        var raw = _packetQueue.Swap();
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
            // 끊긴 세션 패킷은 드롭. 단 세션 종료 흐름의 두 패킷은 세션이 죽은 뒤 도착하므로 항상 통과시킨다:
            //  - NetDisconnect: 종료 트리거(합성 패킷, 세션 제거 후 주입)
            //  - DOExitGameSessionAck: DB 왕복 응답(disconnect 후 Data 계층에서 복귀)
            // 통과시키지 않으면 Player 의 ack 처리와 Eventor 의 archive 제거가 누락되어 archive 가 누수된다.
            if (p.PacketID != (int)Th.EMessageID.NetDisconnect &&
                p.PacketID != (int)Th.EMessageID.DoExitGameSessionAck &&
                !NetworkManager.Instance.IsSessionAlive(p.SessionID))
                continue;

            if (!grouped.TryGetValue(p.SessionID, out var list))
                grouped[p.SessionID] = list = new List<PacketMessage>();
            list.Add(p);
        }
        return grouped;
    }
}
