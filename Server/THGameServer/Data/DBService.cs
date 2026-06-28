using Google.Protobuf;
using Serilog;
using Th;
using TH.Common;
using TH.Server.Logic;

namespace TH.Server.Data;

// OD*(OutGame→Data) 요청을 받아 DO*(Data→OutGame) 로 응답하는 Data(DB) 계층 서비스.
// 모듈러 샤딩: 고정 N 개 worker, sessionID % N 라우팅 → 같은 유저는 항상 같은 worker(단일 스레드 FIFO)
// 가 처리하므로 "유저별 요청 순서" 가 보장된다.
// 응답(DO)은 PacketQueue 로 되돌려, 다음 tick 의 기존 dispatch 흐름(단일 tick 스레드)에서 안전하게 수신된다.
//
// 동시성: Send 는 Player(worker 스레드) / Eventor(tick 스레드) 양쪽에서 호출되나 BlockingCollection 적재라 안전.
//         worker 스레드는 PacketQueue.Enqueue(lock 보호)만 호출하고 전역 / 타 Player 상태를 직접 변경하지 않는다.
//         핸들러 테이블은 생성자에서만 채우고 이후 읽기 전용 → 병렬 dispatch 안전.
public sealed class DBService : Singleton<DBService>
{
    // worker(=샤드) 수. 후속: [DBService] WorkerCount 로 config 화.
    private const int WorkerCount = 4;

    private DBWorker[] _workers = Array.Empty<DBWorker>();

    // packetID → (sessionID, payload) dispatch 델리게이트. 생성자에서만 채우고 이후 읽기 전용.
    private readonly Dictionary<int, Action<long, ReadOnlyMemory<byte>>> _handlers = new();

    private DBService()
    {
        RegisterHandlers();
    }

    public void Init()
    {
        _workers = new DBWorker[WorkerCount];
        for (int i = 0; i < WorkerCount; i++)
        {
            _workers[i] = new DBWorker(i, Dispatch);
            _workers[i].Start();
        }

        Log.Information("DBService started (Workers={N})", WorkerCount);
    }

    public void Shutdown()
    {
        foreach (var worker in _workers)
            worker.Stop();

        Log.Information("DBService shutdown");
    }

    // OD 요청 송신 진입점 — Player(worker) / Eventor(tick) 가 호출.
    // sessionID 로 샤드를 정해 해당 worker mailbox 로 적재 (같은 세션 = 같은 worker = 순서 보장).
    public void Send(long sessionID, int packetID, IMessage msg)
    {
        if (_workers.Length == 0)
        {
            Log.Warning("DBService.Send before Init SessionID={ID} PacketID={PID}", sessionID, packetID);
            return;
        }

        int shard = (int)((ulong)sessionID % (ulong)_workers.Length);
        _workers[shard].Post(new PacketMessage(sessionID, packetID, msg.ToByteArray()));
    }

    // ====================== 핸들러 등록 / dispatch ======================

    private void RegisterHandlers()
    {
        RegisterHandler<ODLoginReq>((int)EMessageID.OdLoginReq, OnODLoginReq);
        RegisterHandler<ODExitGameSessionReq>((int)EMessageID.OdExitGameSessionReq, OnODExitGameSessionReq);
        // 나머지 OD 핸들러는 proto 패킷 추가 시 동일 패턴으로 후속 추가.
    }

    // 핸들러 등록 — 패킷별 ParseFrom 을 한 번만 수행하는 dispatch 델리게이트를 만들어 보관. (LogicEventor.RegisterHandler 미러)
    private void RegisterHandler<T>(int packetID, Action<long, T> handler)
        where T : class, IMessage<T>, new()
    {
        var parser = new MessageParser<T>(() => new T());

        _handlers[packetID] = (sessionID, payload) =>
        {
            T msg;
            try
            {
                msg = parser.ParseFrom(payload.Span);
            }
            catch (InvalidProtocolBufferException ex)
            {
                Log.Warning(ex, "DB packet parse failed SessionID={ID} PacketID={PID}", sessionID, packetID);
                return;
            }

            handler(sessionID, msg);
        };
    }

    // worker 스레드가 호출 — 자기 mailbox 의 요청 1건을 핸들러로 dispatch. 미등록 패킷은 drop.
    private void Dispatch(PacketMessage req)
    {
        if (!_handlers.TryGetValue(req.PacketID, out var invoke))
        {
            Log.Debug("Unregistered DB packet dropped SessionID={ID} PacketID={PID}", req.SessionID, req.PacketID);
            return;
        }

        invoke(req.SessionID, req.Payload);
    }

    // ====================== OD 핸들러 ======================

    // DBSession(실제 DB) 연동 전 stub — ODLoginReq 를 받아 기본값 DOLoginAck 를 만들어 응답.
    private void OnODLoginReq(long sessionID, ODLoginReq msg)
    {
        var ack = new DOLoginAck
        {
            MessageID               = EMessageID.DoLoginAck,
            PID                     = msg.PID,
            AccountID               = 0,
            GameDbID                = 0,
            PlayerName              = string.Empty,
            IsReconnect             = msg.IsReconnect,
            ChannelID               = 0,
            FreeNicknameChangeCount = 0,
            IsNewAccount            = false,
            UpdateTime              = new MDateTime(),
            LanguageID              = msg.LanguageID,
            TotalPlayTime           = 0,
            Authenticated           = true,
        };

        // DO 응답을 PacketQueue 로 되돌린다 → 다음 tick 에 기존 dispatch 가 수신측 핸들러로 전달.
        OutGameService.Instance.EnqueuePacket(sessionID, (int)EMessageID.DoLoginAck, ack.ToByteArray());

        Log.Debug("DBService OD_LOGIN_REQ handled SessionID={ID} PID={PID}", sessionID, msg.PID);
    }

    // DBSession(실제 DB) 연동 전 stub — ODExitGameSessionReq 를 받아 곧바로 DOExitGameSessionAck 로 응답한다.
    // TODO: DB 연동 시 여기서 세션 종료 저장(플레이타임/마지막 위치/세션 로그 등)을 수행한 뒤 ack 를 보낸다.
    private void OnODExitGameSessionReq(long sessionID, ODExitGameSessionReq msg)
    {
        var ack = new DOExitGameSessionAck
        {
            MessageID = EMessageID.DoExitGameSessionAck,
        };

        OutGameService.Instance.EnqueuePacket(sessionID, (int)EMessageID.DoExitGameSessionAck, ack.ToByteArray());

        Log.Debug("DBService OD_EXIT_GAME_SESSION_REQ handled SessionID={ID} AccountID={AID} PID={PID}",
            sessionID, msg.AccountID, msg.PID);
    }
}
