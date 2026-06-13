using Google.Protobuf;
using Serilog;
using Th;
using TH.Common;
using TH.Server.Logic;

namespace TH.Server.Data;

// AD*(App→Data) 요청을 받아 DA*(Data→App) 로 응답하는 Data(DB) 계층 서비스.
// 모듈러 샤딩: 고정 N 개 worker, sessionId % N 라우팅 → 같은 유저는 항상 같은 worker(단일 스레드 FIFO)
// 가 처리하므로 "유저별 요청 순서" 가 보장된다.
// 응답(DA)은 PacketQueue 로 되돌려, 다음 tick 의 기존 dispatch 흐름(단일 tick 스레드)에서 안전하게 수신된다.
//
// 동시성: Send 는 Player(worker 스레드) / Eventor(tick 스레드) 양쪽에서 호출되나 BlockingCollection 적재라 안전.
//         worker 스레드는 PacketQueue.Enqueue(lock 보호)만 호출하고 전역 / 타 Player 상태를 직접 변경하지 않는다.
//         핸들러 테이블은 생성자에서만 채우고 이후 읽기 전용 → 병렬 dispatch 안전.
public sealed class DBService : Singleton<DBService>
{
    // worker(=샤드) 수. 후속: [DBService] WorkerCount 로 config 화.
    private const int WorkerCount = 4;

    private DBWorker[] _workers = Array.Empty<DBWorker>();

    // packetId → (sessionId, payload) dispatch 델리게이트. 생성자에서만 채우고 이후 읽기 전용.
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

    // AD 요청 송신 진입점 — Player(worker) / Eventor(tick) 가 호출.
    // sessionId 로 샤드를 정해 해당 worker mailbox 로 적재 (같은 세션 = 같은 worker = 순서 보장).
    public void Send(long sessionId, int packetId, IMessage msg)
    {
        if (_workers.Length == 0)
        {
            Log.Warning("DBService.Send before Init SessionId={Id} PacketId={Pid}", sessionId, packetId);
            return;
        }

        int shard = (int)((ulong)sessionId % (ulong)_workers.Length);
        _workers[shard].Post(new PacketMessage(sessionId, packetId, msg.ToByteArray()));
    }

    // ====================== 핸들러 등록 / dispatch ======================

    private void RegisterHandlers()
    {
        RegisterHandler<ADLoginReq>((int)EMessageID.AdLoginReq, OnADLoginReq);
        // 나머지 AD 핸들러(PlayerInfo / PlayerHero / PlayerEquipItem / EndofGameSession)는 동일 패턴으로 후속 추가.
    }

    // 핸들러 등록 — 패킷별 ParseFrom 을 한 번만 수행하는 dispatch 델리게이트를 만들어 보관. (LogicEventor.RegisterHandler 미러)
    private void RegisterHandler<T>(int packetId, Action<long, T> handler)
        where T : class, IMessage<T>, new()
    {
        var parser = new MessageParser<T>(() => new T());

        _handlers[packetId] = (sessionId, payload) =>
        {
            T msg;
            try
            {
                msg = parser.ParseFrom(payload.Span);
            }
            catch (InvalidProtocolBufferException ex)
            {
                Log.Warning(ex, "DB packet parse failed SessionId={Id} PacketId={Pid}", sessionId, packetId);
                return;
            }

            handler(sessionId, msg);
        };
    }

    // worker 스레드가 호출 — 자기 mailbox 의 요청 1건을 핸들러로 dispatch. 미등록 패킷은 drop.
    private void Dispatch(PacketMessage req)
    {
        if (!_handlers.TryGetValue(req.PacketId, out var invoke))
        {
            Log.Debug("Unregistered DB packet dropped SessionId={Id} PacketId={Pid}", req.SessionId, req.PacketId);
            return;
        }

        invoke(req.SessionId, req.Payload);
    }

    // ====================== AD 핸들러 ======================

    // DBSession(실제 DB) 연동 전 stub — ADLoginReq 를 받아 기본값 DALoginAck 를 만들어 응답.
    private void OnADLoginReq(long sessionId, ADLoginReq msg)
    {
        var ack = new DALoginAck
        {
            MessageID               = EMessageID.DaLoginAck,
            PID                     = msg.PID,
            AccountId               = 0,
            GameDbId                = 0,
            PlayerName              = string.Empty,
            IsReconnect             = msg.IsReconnect,
            ChannelID               = 0,
            FreeNicknameChangeCount = 0,
            IsNewAccount            = false,
            UpdateTime              = new MDateTime(),
            LanguageID              = msg.LanguageID,
            TotalPlayTime           = 0,
            Authenticated           = true,
            PlatformType            = msg.PlatformType,
        };

        // DA 응답을 PacketQueue 로 되돌린다 → 다음 tick 에 기존 dispatch 가 수신측 핸들러로 전달.
        PacketQueue.Instance.Enqueue(sessionId, (int)EMessageID.DaLoginAck, ack.ToByteArray());

        Log.Debug("DBService AD_LOGIN_REQ handled SessionId={Id} PID={Pid}", sessionId, msg.PID);
    }
}
