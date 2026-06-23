using System.Collections.Concurrent;
using System.Diagnostics;
using Serilog;
using TH.Common;
using TH.Common.Time;

namespace TH.Server.Logic;

// InGame(필드/룸) 시뮬레이션 서비스. OutGameService 와 동형으로 phase(Prepare → Work)를 돌리되,
// 병렬 단위가 "Player" 가 아니라 "GameRoom" 이다. OutGameService(300ms)와 독립된 자체 100ms tick 스레드.
//
// 시간 소스는 OutGame 과 동일하게 TimeManager.TickMillis()(monotonic, 시스템 시계 변경 무관)다.
// 매 ~100ms 주기로 ProcessTick 을 1회 돌리되, dt 로는 "지난 tick 이후 실제 경과 시간"(가변 dt)을 넘긴다.
public sealed class InGameService : Singleton<InGameService>
{
    public const int TickIntervalMs = 100;

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
    public void EnqueuePacket(long sessionID, int packetID, byte[] payload)
        => _packetQueue.Enqueue(sessionID, packetID, payload);

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
        // monotonic 시간원 — TimeManager.TickMillis()(Environment.TickCount64)는 시스템 시계
        // 점프/역행에 영향받지 않는다. 단위는 ms.
        long lastTickMs = TimeManager.Instance.TickMillis();

        while (!_stopping)
        {
            // 다음 ~100ms 경계까지 sleep+spin 으로 대기.
            WaitUntil(lastTickMs + TickIntervalMs);
            if (_stopping)
                break;

            long nowMs = TimeManager.Instance.TickMillis();
            long dtMs = nowMs - lastTickMs;   // 지난 tick 이후 실제 경과 시간(가변 dt)
            lastTickMs = nowMs;

            long tickStart = Stopwatch.GetTimestamp();
            try
            {
                ProcessTick(dtMs);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "InGameService ProcessTick exception");
            }

            var elapsed = Stopwatch.GetElapsedTime(tickStart);
            if (elapsed.TotalMilliseconds > TickIntervalMs)
                Log.Warning("InGame tick overrun: {Elapsed:F1}ms (target {Target}ms)",
                    elapsed.TotalMilliseconds, TickIntervalMs);
        }
    }

    // 다음 tick 경계(deadline)까지 대기 — sleep+spin 하이브리드.
    // 경계 정밀도는 TickMillis() 분해능(Windows 약 15.6ms)에 묶인다.
    private void WaitUntil(long deadlineMs)
    {
        while (!_stopping)
        {
            long left = deadlineMs - TimeManager.Instance.TickMillis();
            if (left <= 0)
                break;

            if (left > SpinThresholdMs)
                Thread.Sleep(1);     // 경계 한참 전 — CPU 양보 (quantum 분해능 오차는 아래 spin 이 흡수)
            else
                Thread.SpinWait(64); // 경계 직전 — busy-wait 로 경계 정확도 확보
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
        // (1) 네트워크 패킷 라우팅: sessionID → SessionRoomMap → 해당 룸 inbox 로 PacketRoomJob 적재.
        var raw = _packetQueue.Swap();
        foreach (var p in raw)
        {
            if (_sessionRoomMap.TryGet(p.SessionID, out var roomID) && _repo.Find(roomID) is { } room)
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
