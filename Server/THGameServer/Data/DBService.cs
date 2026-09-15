using Google.Protobuf;
using Serilog;
using Th;
using TH.Common;
using TH.Server.Logic;

namespace TH.Server.Data;

// Data(DB) 계층 서비스. OD 요청(OutGame → Data)을 받아 DO 응답(Data → OutGame)을 돌려준다.
// 샤딩: sessionID % N으로 worker를 고른다. 같은 유저는 항상 같은 worker(단일 스레드 FIFO)가 처리하므로
// 유저별 요청 순서가 보장된다. 응답은 PacketQueue에 넣어 다음 tick의 dispatch 흐름에서 받는다.
// 동시성: Send는 worker 스레드와 tick 스레드 양쪽에서 불리지만 BlockingCollection에 넣기만 하므로 안전하다.
// worker 스레드는 PacketQueue.Enqueue(lock 보호)만 호출하고 다른 상태는 건드리지 않는다.
// 핸들러 테이블은 생성자에서만 채우고 이후 읽기 전용이다. 로그인 흐름은 docs/server-logic-architecture.md §2.5 참조.
public sealed class DBService : Singleton<DBService>
{
    // worker(샤드) 수. TODO: config([ThreadCount] DB)에서 읽도록 변경.
    private const int WorkerCount = 4;

    private DBWorker[] _workers = Array.Empty<DBWorker>();

    // packetID별 dispatch 델리게이트(sessionID, payload). 생성자에서만 채우고 이후 읽기 전용.
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

    // OD 요청을 보내는 진입점. Player(worker 스레드)와 Eventor(tick 스레드)가 호출한다.
    // sessionID로 샤드를 정해 해당 worker의 mailbox에 넣는다. 같은 세션은 같은 worker로 가므로 순서가 보장된다.
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
    }

    // 핸들러 등록. 패킷별 ParseFrom을 한 번만 하는 dispatch 델리게이트를 만들어 둔다. LogicEventor.RegisterHandler와 같은 구조.
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

    // worker 스레드가 호출한다. 자기 mailbox의 요청 1건을 핸들러로 dispatch한다. 미등록 패킷은 버린다.
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

    // 실제 DB 연동 전 stub. ODLoginReq를 받아 기본값 DOLoginAck로 응답한다.
    private void OnODLoginReq(long sessionID, ODLoginReq msg)
    {
        var ack = new DOLoginAck
        {
            MessageID               = EMessageID.DoLoginAck,
            PID                     = msg.PID,
            // stub. DB 연동 전까지 sessionID를 그대로 AccountID로 쓴다(세션별 구분·디버깅 편의).
            AccountID               = sessionID,
            GameDbID                = 0,
            PlayerName              = $"player_{sessionID}",
            IsReconnect             = msg.IsReconnect,
            ChannelID               = 0,
            FreeNicknameChangeCount = 0,
            IsNewAccount            = false,
            UpdateTime              = new MDateTime(),
            LanguageID              = msg.LanguageID,
            TotalPlayTime           = 0,
            Authenticated           = true,
        };

        // DO 응답을 PacketQueue에 넣는다. 다음 tick의 dispatch가 수신측 핸들러로 전달한다.
        OutGameService.Instance.EnqueuePacket(sessionID, (int)EMessageID.DoLoginAck, ack.ToByteArray());

        Log.Debug("DBService OD_LOGIN_REQ handled SessionID={ID} PID={PID}", sessionID, msg.PID);
    }

    // 실제 DB 연동 전 stub. ODExitGameSessionReq를 받아 곧바로 DOExitGameSessionAck로 응답한다.
    // TODO: DB 연동 시 세션 종료 정보(플레이타임, 마지막 위치, 세션 로그)를 저장한 뒤 ack를 보낸다.
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
