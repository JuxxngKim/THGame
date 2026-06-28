using System.Diagnostics;
using Google.Protobuf;
using Serilog;
using Th;
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

    // InGame 패킷 입력 큐 — OutGame 과 동일한 더블버퍼 PacketQueue 재사용. 클라발 게임플레이 패킷과
    // OutGame 발 제어 패킷(OIEnterReq/OILeaveReq)이 모두 이 큐로 들어온다(입력은 전부 패킷).
    private readonly PacketQueue _packetQueue = new();

    // 제어 패킷(OIEnterReq/OILeaveReq) 핸들러 테이블 — packetID → dispatch. Prepare(단일 스레드)에서만 호출.
    private readonly Dictionary<int, Action<PacketMessage>> _handlers = new();

    private readonly SessionRoomMap _sessionRoomMap = new();
    private readonly RoomRepository _repo = new();

    private Thread? _mainThread;
    private volatile bool _stopping;

    private InGameService()
    {
        // 크로스도메인 제어 패킷 배선. enter/leave 는 SessionRoomMap/RoomRepository(공유 상태) 변경이
        // 필요하므로 룸이 아니라 여기 Prepare 에서 처리하고, 룸 내부(Character) 변경만 룸 Inbox 로 위임한다.
        Register<OIEnterReq>((int)EMessageID.OiEnterReq, OnEnter);
        Register<OILeaveReq>((int)EMessageID.OiLeaveReq, OnLeave);
        Register<OIExitGameSessionReq>((int)EMessageID.OiExitGameSessionReq, OnExitGameSession);
    }

    // ====================== 외부 진입점 (멀티스레드 안전) ======================

    // 네트워크(IO) 스레드 + OutGame Work 스레드에서 InGame 패킷을 적재. PacketQueue.Enqueue 는 lock 보호.
    // 클라발 게임플레이 패킷도, OutGame 발 제어 패킷(OIEnterReq/OILeaveReq)도 동일한 이 진입점으로.
    public void EnqueuePacket(long sessionID, int packetID, byte[] payload)
        => _packetQueue.Enqueue(sessionID, packetID, payload);

    // ====================== 제어 패킷 핸들러 (Prepare 단일 스레드) ======================

    // 핸들러 등록 — 패킷별 ParseFrom 을 한 번만 수행하는 dispatch 델리게이트를 만들어 보관.
    // (OutGame Player.Register / LogicEventor.RegisterHandler 와 동형 패턴.)
    private void Register<T>(int packetID, Action<PacketMessage, T> handler)
        where T : class, IMessage<T>, new()
    {
        var parser = new MessageParser<T>(() => new T());

        _handlers[packetID] = packet =>
        {
            T msg;
            try
            {
                msg = parser.ParseFrom(packet.Payload);
            }
            catch (InvalidProtocolBufferException ex)
            {
                Log.Warning(ex, "InGame control packet parse failed SessionID={ID} PacketID={PID}",
                    packet.SessionID, packetID);
                return;
            }

            handler(packet, msg);
        };
    }

    // 진입 — 공유 상태(SessionRoomMap/RoomRepository) 변경은 여기서(Prepare). 룸이 없으면 생성한다.
    // 실제 Character 생성은 룸 Inbox 로 위임 → 룸 Work 에서 single-writer 로 처리.
    private void OnEnter(PacketMessage packet, OIEnterReq msg)
    {
        var roomID = new RoomID(msg.RoomID);
        var room = _repo.GetOrCreate(roomID);
        _sessionRoomMap.Set(packet.SessionID, roomID);
        room.Inbox.Enqueue(packet);
    }

    // 이탈 — SessionRoomMap 에서 현재 룸을 찾아 제거. 세션이 어느 룸에도 없으면 no-op
    // (이미 이탈했거나 진입 전 disconnect). Character 제거는 룸 Inbox 로 위임.
    private void OnLeave(PacketMessage packet, OILeaveReq msg)
    {
        _ = msg;
        if (!_sessionRoomMap.TryGet(packet.SessionID, out var roomID))
            return;

        _sessionRoomMap.Remove(packet.SessionID);
        _repo.Find(roomID)?.Inbox.Enqueue(packet);
    }

    // 세션 종료 — disconnect 의 DB 왕복(ODExitGameSessionReq/DOExitGameSessionAck) 후 OutGame Arrange 가
    // 보낸다. OnLeave 와 동일하게 SessionRoomMap 에서 룸을 찾아 제거하고 Character 제거는 룸 Inbox 로 위임.
    // 어느 룸에도 없으면 no-op(필드 진입 전 종료).
    private void OnExitGameSession(PacketMessage packet, OIExitGameSessionReq msg)
    {
        _ = msg;
        if (!_sessionRoomMap.TryGet(packet.SessionID, out var roomID))
            return;

        _sessionRoomMap.Remove(packet.SessionID);
        _repo.Find(roomID)?.Inbox.Enqueue(packet);
    }

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

    // Prepare (단일 tick 스레드) — SessionRoomMap/RoomRepository mutate 와 룸 Inbox 적재는 전부 여기서.
    private void Prepare()
    {
        var raw = _packetQueue.Swap();
        foreach (var p in raw)
        {
            // 제어 패킷(OIEnterReq/OILeaveReq): 공유 상태 변경 + 룸 Inbox 적재.
            if (_handlers.TryGetValue(p.PacketID, out var handle))
            {
                try
                {
                    handle(p);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "InGame control handler exception SessionID={ID} PacketID={PID}",
                        p.SessionID, p.PacketID);
                }
                continue;
            }

            // 게임플레이 패킷: 라우팅만 — 실제 처리는 룸 Work(DrainInbox)에서 병렬로.
            // 어느 룸에도 없는 세션의 패킷은 드롭(진입 전 도착 / 이탈 후 잔여).
            if (_sessionRoomMap.TryGet(p.SessionID, out var roomID) && _repo.Find(roomID) is { } room)
                room.Inbox.Enqueue(p);
        }
        raw.Clear();
    }

    // Work (병렬) — 룸들을 직접 병렬 실행. 룸끼리 병렬, 한 룸은 한 스레드(룸 single-writer 보장).
    // Parallel.ForEach 가 동기 반환할 때까지 블로킹 → 모든 룸 Tick 이 끝나야 한 tick 이 종료된다.
    // SessionRoomMap 은 read-only.
    //
    // 한계(인지하고 남김): "한 틱 = 전체 룸의 배리어"라 무거운 단일 룸(대규모 인원/전투)이 그 틱의
    // tail-latency 를 지배하면(straggler) 나머지 룸이 끝나도 배리어에서 대기한다.
    private void Work(long dtMs)
    {
        var rooms = _repo.Rooms;
        if (rooms.Count == 0)
            return;

        Parallel.ForEach(rooms, room => room.Tick(dtMs));
    }
}
