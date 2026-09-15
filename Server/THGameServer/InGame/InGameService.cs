using System.Diagnostics;
using Google.Protobuf;
using Serilog;
using Th;
using TH.Common;
using TH.Common.Time;

namespace TH.Server.Logic;

// InGame(필드/룸) 시뮬레이션 서비스. OutGameService와 같은 구조로 Prepare → Work를 돌리되 병렬 단위가 GameRoom이다.
// OutGameService(300ms)와 독립된 자체 100ms tick 스레드. tick 루프와 분배 구조는 docs/server-logic-architecture.md §6 참조.
// dt로는 "지난 tick 이후 실제 경과 시간"(가변)을 넘긴다.
public sealed class InGameService : Singleton<InGameService>
{
    public const int TickIntervalMs = 100;

    // sleep+spin 전환 기준. 다음 틱까지 이만큼 이상 남으면 Sleep, 그보다 적으면 spin.
    // Windows 타이머 quantum(약 15.6ms)보다 조금 크게 잡아 Sleep 오차를 spin 구간이 흡수하게 한다.
    private const int SpinThresholdMs = 16;

    // InGame 패킷 입력 큐. OutGame과 같은 더블버퍼 PacketQueue를 재사용한다.
    // 클라가 보낸 게임플레이 패킷과 OutGame이 보낸 제어 패킷(OIEnterReq/OILeaveReq)이 모두 이 큐로 들어온다.
    private readonly PacketQueue _packetQueue = new();

    // 제어 패킷(OIEnterReq/OILeaveReq) 핸들러 테이블. packetID → dispatch. Prepare(단일 스레드)에서만 호출한다.
    private readonly Dictionary<int, Action<PacketMessage>> _handlers = new();

    private readonly SessionRoomMap _sessionRoomMap = new();
    private readonly RoomRepository _repo = new();

    private Thread? _mainThread;
    private volatile bool _stopping;

    private InGameService()
    {
        // OutGame → InGame 제어 패킷 등록. enter/leave는 공유 상태(SessionRoomMap/RoomRepository)를 바꿔야 하므로
        // 룸이 아니라 여기 Prepare에서 처리하고, 룸 내부(Character) 변경만 룸 Inbox로 넘긴다.
        Register<OIEnterReq>((int)EMessageID.OiEnterReq, OnEnter);
        Register<OILeaveReq>((int)EMessageID.OiLeaveReq, OnLeave);
        Register<OIExitGameSessionReq>((int)EMessageID.OiExitGameSessionReq, OnExitGameSession);
    }

    // ====================== 외부 진입점 (멀티스레드 안전) ======================

    // 네트워크(IO) 스레드와 OutGame Work 스레드가 InGame 패킷을 넣는 유일한 진입점. PacketQueue.Enqueue는 lock으로 보호된다.
    // 클라 게임플레이 패킷도, OutGame 제어 패킷(OIEnterReq/OILeaveReq)도 모두 여기로 들어온다.
    public void EnqueuePacket(long sessionID, int packetID, byte[] payload)
        => _packetQueue.Enqueue(sessionID, packetID, payload);

    // ====================== 제어 패킷 핸들러 (Prepare 단일 스레드) ======================

    // 핸들러 등록. 패킷별 ParseFrom을 한 번만 수행하는 dispatch 델리게이트를 만들어 둔다.
    // OutGame의 Player.Register / LogicEventor.RegisterHandler와 같은 패턴이다.
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

    // 입장. 공유 상태(SessionRoomMap/RoomRepository) 변경은 여기 Prepare에서 한다. 룸이 없으면 만든다.
    // 실제 Character 생성은 룸 Inbox로 넘겨 룸 Work에서 single-writer로 처리한다.
    private void OnEnter(PacketMessage packet, OIEnterReq msg)
    {
        var roomID = new RoomID(msg.RoomID);
        var room = _repo.GetOrCreate(roomID);
        _sessionRoomMap.Set(packet.SessionID, roomID);
        room.Inbox.Enqueue(packet);
    }

    // 퇴장. SessionRoomMap에서 현재 룸을 찾아 제거한다. 세션이 어느 룸에도 없으면 no-op
    // (이미 나갔거나 입장 전 disconnect). Character 제거는 룸 Inbox로 넘긴다.
    private void OnLeave(PacketMessage packet, OILeaveReq msg)
    {
        _ = msg;
        if (!_sessionRoomMap.TryGet(packet.SessionID, out var roomID))
            return;

        _sessionRoomMap.Remove(packet.SessionID);
        _repo.Find(roomID)?.Inbox.Enqueue(packet);
    }

    // 세션 종료. disconnect의 DB 왕복(ODExitGameSessionReq/DOExitGameSessionAck)이 끝난 뒤 OutGame Arrange가 보낸다.
    // OnLeave와 같이 SessionRoomMap에서 룸을 찾아 제거하고 Character 제거는 룸 Inbox로 넘긴다.
    // 어느 룸에도 없으면 no-op(필드 입장 전 종료).
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
        // monotonic 시간 기준. TimeManager.TickMillis()(Environment.TickCount64)는 시스템 시계가
        // 바뀌어도 영향받지 않는다. 단위는 ms.
        long lastTickMs = TimeManager.Instance.TickMillis();

        while (!_stopping)
        {
            // 다음 100ms 경계까지 sleep+spin으로 대기.
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

    // 다음 tick 경계(deadline)까지 sleep+spin으로 대기.
    // 경계 정밀도는 TickMillis() 분해능(Windows 약 15.6ms)에 묶인다.
    private void WaitUntil(long deadlineMs)
    {
        while (!_stopping)
        {
            long left = deadlineMs - TimeManager.Instance.TickMillis();
            if (left <= 0)
                break;

            if (left > SpinThresholdMs)
                Thread.Sleep(1);     // 경계가 멀면 CPU 양보. quantum 오차는 아래 spin이 흡수한다.
            else
                Thread.SpinWait(64); // 경계 직전에는 busy-wait로 정확도를 확보한다.
        }
    }

    // 한 tick 본체. Prepare(단일 스레드) → Work(룸 병렬). 타이밍과 예외 처리는 MainLoop가 맡는다.
    private void ProcessTick(long dtMs)
    {
        Prepare();
        Work(dtMs);
    }

    // Prepare(단일 tick 스레드). SessionRoomMap/RoomRepository 변경과 룸 Inbox에 넣는 일은 전부 여기서 한다.
    private void Prepare()
    {
        var raw = _packetQueue.Swap();
        foreach (var p in raw)
        {
            // 제어 패킷(OIEnterReq/OILeaveReq): 공유 상태를 바꾸고 룸 Inbox에 넣는다.
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

            // 게임플레이 패킷: 라우팅만 한다. 실제 처리는 룸 Work(DrainInbox)에서 병렬로.
            // 어느 룸에도 없는 세션의 패킷은 버린다(입장 전 도착 / 퇴장 후 잔여).
            if (_sessionRoomMap.TryGet(p.SessionID, out var roomID) && _repo.Find(roomID) is { } room)
                room.Inbox.Enqueue(p);
        }
        raw.Clear();
    }

    // Work(병렬). 룸끼리는 병렬, 한 룸은 한 스레드(룸 single-writer 보장). SessionRoomMap은 읽기만 한다.
    // Parallel.ForEach가 반환할 때까지 블로킹되므로 모든 룸 Tick이 끝나야 한 tick이 끝난다.
    //
    // 알고 남긴 한계: "한 틱 = 전체 룸의 barrier"라 무거운 룸 하나(대규모 인원/전투)가 straggler가 되면
    // 나머지 룸이 끝나도 barrier에서 기다린다.
    private void Work(long dtMs)
    {
        var rooms = _repo.Rooms;
        if (rooms.Count == 0)
            return;

        Parallel.ForEach(rooms, room => room.Tick(dtMs));
    }
}
