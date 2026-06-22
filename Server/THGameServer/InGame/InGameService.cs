using System.Collections.Concurrent;
using System.Diagnostics;
using Serilog;
using TH.Common;

namespace TH.Server.Logic;

// InGame(필드/룸) 시뮬레이션 서비스. OutGameService 와 동형으로 phase(Prepare → Work)를 돌리되,
// 병렬 단위가 "Player" 가 아니라 "GameRoom" 이다. OutGameService(300ms)와 독립된 자체 100ms tick 스레드.
//
// 틱 모델은 OutGame(tick skip + UnixMillis)과 의도적으로 다르다 — 게임 시뮬은 결정성/물리 적분
// 안정성이 중요하므로 Stopwatch(monotonic) 기반 + 고정 dt(100ms) catch-up step 을 실제로 실행한다.
public sealed class InGameService : Singleton<InGameService>
{
    public const int TickIntervalMs = 100;

    // catch-up 상한 — 밀려도 한 루프에서 최대 2스텝만 따라잡고 나머지 논리 시간은 버린다(frame drop).
    // 무한 catch-up 은 처리가 더 밀리는 death spiral 을 부르므로 절대 허용하지 않는다.
    private const int MaxCatchUp = 2;

    // 각 catch-up 스텝에 넘기는 dt 는 항상 고정 100ms — 가변 dt 금지(결정성/적분 안정성).
    private const long FixedDtMs = TickIntervalMs;

    // sleep+spin 하이브리드 임계 — 다음 틱 경계까지 이만큼 이상 남으면 Sleep(양보), 미만이면 spin.
    // Windows 타이머 quantum(~15.6ms)보다 약간 크게 잡아 Sleep 분해능 오차를 spin 구간이 흡수한다.
    private const int SpinThresholdMs = 16;

    // 네트워크발 InGame 패킷 입력 큐 — OutGame 과 동일한 더블버퍼 PacketQueue 재사용.
    private readonly PacketQueue _packetQueue = new();

    // 크로스도메인(OutGame → InGame) 진입/이탈/포탈 명령 큐. 외부 스레드는 Enqueue 만, 처리는 Prepare.
    private readonly ConcurrentQueue<IRoomCommand> _commandQueue = new();

    private readonly IInterestManagement _interest = new BroadcastInterest();
    private readonly SessionRoomMap _sessionRoomMap = new();
    private readonly RoomRepository _repo;
    private readonly IRoomScheduler _scheduler = new ParallelRoomScheduler();

    private Thread? _mainThread;
    private volatile bool _stopping;

    private InGameService()
    {
        _repo = new RoomRepository(_interest);
    }

    // ====================== 외부 진입점 (멀티스레드 안전) ======================

    // 네트워크(IO) 스레드에서 InGame 대역 패킷을 적재. PacketQueue.Enqueue 는 lock 보호.
    public void EnqueuePacket(long sessionID, int packetId, byte[] payload)
        => _packetQueue.Enqueue(sessionID, packetId, payload);

    // OutGame Player(Work 스레드) 등에서 진입을 요청. 직접 참조 없이 명령 큐로만 전달.
    public void EnqueueEnter(long sessionID, RoomID roomID)
        => _commandQueue.Enqueue(new EnterCommand(sessionID, roomID));

    // 이탈/disconnect 시 호출. 세션이 어느 룸에도 없으면 Prepare 에서 no-op 처리된다.
    public void EnqueueLeave(long sessionID)
        => _commandQueue.Enqueue(new LeaveCommand(sessionID));

    // ====================== lifecycle ======================

    public void Init()
    {
        _mainThread = new Thread(MainLoop) { IsBackground = true, Name = "InGameMain" };
        _mainThread.Start();

        Log.Information("InGameService started (Tick={Tick}ms)", TickIntervalMs);
    }

    public void Shutdown()
    {
        _stopping = true;
        _mainThread?.Join();
        Log.Information("InGameService shutdown");
    }

    // ====================== tick 루프 ======================

    private void MainLoop()
    {
        // Stopwatch monotonic — DateTime 금지(시스템 시계 점프/역행에 영향받지 않음).
        long tickTicks = Stopwatch.Frequency / (1000 / TickIntervalMs); // 100ms 에 해당하는 Stopwatch ticks
        long last = Stopwatch.GetTimestamp();
        long accumulator = 0;

        while (!_stopping)
        {
            long now = Stopwatch.GetTimestamp();
            accumulator += now - last;
            last = now;

            // catch-up: 누적 시간이 한 틱 이상이면 따라잡되, 매 스텝 dt 는 항상 고정 100ms.
            int steps = 0;
            while (accumulator >= tickTicks && steps < MaxCatchUp)
            {
                long tickStart = Stopwatch.GetTimestamp();
                try
                {
                    ProcessTick(FixedDtMs);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "InGameService ProcessTick exception");
                }

                var elapsed = Stopwatch.GetElapsedTime(tickStart);
                if (elapsed.TotalMilliseconds > TickIntervalMs)
                    Log.Warning("InGame tick overrun: {Elapsed:F1}ms (target {Target}ms)",
                        elapsed.TotalMilliseconds, TickIntervalMs);

                accumulator -= tickTicks;
                steps++;
            }

            // frame drop — 상한 초과로 여전히 한 틱 이상 남았으면 밀린 논리 시간을 버린다(death spiral 방어).
            if (accumulator >= tickTicks)
            {
                long dropped = accumulator / tickTicks;
                accumulator %= tickTicks;
                Log.Warning("InGame catch-up cap hit — dropping {Dropped} logical tick(s)", dropped);
            }

            SleepUntilNextTick(tickTicks, accumulator);
        }
    }

    // 다음 100ms 경계까지 대기 — sleep+spin 하이브리드로 경계 정확도 확보.
    private void SleepUntilNextTick(long tickTicks, long accumulator)
    {
        long remaining = tickTicks - accumulator;
        if (remaining <= 0)
            return; // 이미 다음 틱 분량이 차 있으면 즉시 재처리

        long deadline = Stopwatch.GetTimestamp() + remaining;
        long spinThreshold = Stopwatch.Frequency / 1000 * SpinThresholdMs; // SpinThresholdMs 에 해당하는 ticks

        while (!_stopping)
        {
            long left = deadline - Stopwatch.GetTimestamp();
            if (left <= 0)
                break;

            if (left > spinThreshold)
                Thread.Sleep(1);     // 경계 한참 전 — CPU 양보 (quantum 분해능 오차는 아래 spin 이 흡수)
            else
                Thread.SpinWait(64); // 경계 직전 — busy-wait 로 100ms 경계 정확도 확보
        }
    }

    // 한 tick 본체 — Prepare(단일 스레드) → Work(룸 병렬). 타이밍/예외 격리는 MainLoop 책임.
    private void ProcessTick(long dtMs)
    {
        Prepare();
        Work(dtMs);
    }

    // Prepare (단일 tick 스레드) — SessionRoomMap mutate 와 룸 inbox 적재는 전부 여기서.
    private void Prepare()
    {
        // (1) 네트워크 패킷 라우팅: sessionId → SessionRoomMap → 해당 룸 inbox 로 PacketRoomJob 적재.
        var raw = _packetQueue.Swap();
        foreach (var p in raw)
        {
            if (_sessionRoomMap.TryGet(p.SessionId, out var roomID) && _repo.Find(roomID) is { } room)
                room.JobQueue.Enqueue(new PacketRoomJob(p));
            // 어느 룸에도 없는 세션의 패킷은 드롭(진입 전 도착 / 이탈 후 잔여).
        }
        raw.Clear();

        // (2) 크로스도메인 명령 처리 — enterRoom/leaveRoom/포탈. SessionRoomMap mutate 는 오직 여기.
        while (_commandQueue.TryDequeue(out var cmd))
        {
            try
            {
                cmd.Apply(_sessionRoomMap, _repo);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Room command exception Cmd={Cmd}", cmd.GetType().Name);
            }
        }
    }

    // Work (병렬) — 룸들을 스케줄러로 실행. 룸끼리 병렬, 한 룸은 한 스레드. SessionRoomMap 은 read-only.
    private void Work(long dtMs) => _scheduler.Run(dtMs, _repo.Rooms);
}
