using System.Diagnostics;
using Serilog;
using TH.Common;
using TH.Common.Network;
using TH.Common.Time;

namespace TH.Server.Logic;

// OutGame tick 서비스. tick마다 Event → Prepare → Work → Arrange 순으로 phase를 돈다.
// Work는 세션 워커 단위 병렬 처리이고 나머지는 단일 tick 스레드. 상세는 docs/server-logic-architecture.md §1.
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

    // 외부(IO 스레드, Data 계층)에서 tick 입력 큐에 패킷을 넣는 유일한 진입점.
    // 내부 큐 구현(PacketQueue)을 감춘다. Enqueue는 lock으로 보호되어 여러 스레드에서 호출해도 안전하다.
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
        // 다음 tick 예정 시각의 시작점. 이후 매 tick마다 monotonic 시각 기준으로 += 하며 앞으로 옮긴다.
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

                // 다음 예정 시각을 phase 처리 "전에" 앞으로 옮긴다.
                // - drift 방지: 깨어난 실제 시각이 아니라 직전 예정 시각에 더한다. Sleep 오차가 누적되지 않는다.
                // - catch-up: 많이 밀렸으면 놓친 tick을 한꺼번에 돌리지 않고 건너뛴다.
                // - 핫루프 방지: phase에서 예외가 나도 예정 시각이 이미 옮겨져 다음 루프가 바로 재처리하지 않는다.
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

    // 한 tick 본체. Event → Prepare → Work → Arrange. 타이밍과 예외 처리는 MainLoop 책임.
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
            // 끊긴 세션의 패킷은 버린다. 단 세션 종료 흐름의 두 패킷은 세션이 닫힌 뒤 도착하므로 항상 통과시킨다.
            //  - NetDisconnect: 종료 트리거. 세션 제거 후 서버가 직접 만들어 넣는 패킷.
            //  - DOExitGameSessionAck: disconnect 후 Data 계층에서 돌아오는 DB 응답.
            // 통과시키지 않으면 Player의 ack 처리와 Eventor의 archive 제거가 빠져 archive가 누수된다.
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
