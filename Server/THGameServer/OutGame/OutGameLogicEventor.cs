using Google.Protobuf;
using Serilog;
using Th;
using TH.Common;
using TH.Common.Network;
using TH.Server.Game;
using TH.Common.Time;

namespace TH.Server.Logic;

// OutGame(로그인/세션 관리) 도메인 Eventor. PlayerArchive를 소유하고 로그인 핸드셰이크와 세션 종료 흐름을 담당한다.
// phase별 핸들러 배치와 흐름은 docs/server-logic-architecture.md §2.
public sealed class OutGameLogicEventor : LogicEventor
{
    private const long ServerInfoSyncMs    = 5_000;
    private const long BiCurrentUserSyncMs = 60_000;
    private const long ServerAliveSyncMs   = 1_000;
    private const long PlayerCountSyncMs   = 5_000;

    // 로그인 세션 타임아웃. DOLoginAck가 이 시간 안에 오지 않으면 세션을 정리한다.
    private const long LoginTimeoutMs        = 10_000;
    private const long LoginTimeoutCheckMs   = 1_000;

    private long _lastServerInfoSyncTime;
    private long _lastBiCurrentUserSyncTime;
    private long _nextPlayerCountSyncTime;
    private long _nextServerAliveSyncTime;
    private long _nextLoginTimeoutCheck;

    // ISessionWorker(Player + LoginSession) 집합. 이 Eventor가 소유한다.
    // 등록/제거/조회는 전부 단일 tick 스레드(Prepare/Event/Arrange)에서만 한다.
    private readonly PlayerArchive _archive = new();

    // Work phase 병렬 실행기(상태 없음). 순회 대상은 Work에서 _archive.Values로 넘긴다.
    private readonly PlayerWorkExecutor _workExecutor = new();

    public OutGameLogicEventor()
    {
        long now = TimeManager.Instance.UnixMillis();
        _lastServerInfoSyncTime    = now;
        _lastBiCurrentUserSyncTime = now;
        _nextPlayerCountSyncTime   = now + PlayerCountSyncMs;
        _nextServerAliveSyncTime   = now + ServerAliveSyncMs;
        // 로그인 타임아웃은 Event(tickMs)의 monotonic 시간 기준(TickMillis)으로 비교하므로 같은 기준으로 초기화한다.
        _nextLoginTimeoutCheck     = TimeManager.Instance.TickMillis() + LoginTimeoutCheckMs;

        // NetDisconnect(Prepare). Player면 제거를 미루고(DB 왕복 후 정리), LoginSession이나 미등록이면 즉시 제거한다.
        RegisterHandler<NetDisconnect>((int)EMessageID.NetDisconnect,
            OnNetDisconnect, ELogicEvent.Prepare);

        // DOExitGameSessionAck(Arrange). 세션 종료 저장이 끝난 시점에 archive에서 Player를 제거하고 InGame 캐릭터를 정리한다.
        // Player의 ack 로그는 같은 tick의 Work에서 먼저 찍힌다.
        RegisterHandler<DOExitGameSessionAck>((int)EMessageID.DoExitGameSessionAck,
            OnDOExitGameSessionAck, ELogicEvent.Arrange);

        RegisterHandler<NetAliveReq>((int)EMessageID.NetAliveReq,
            OnAliveReq, ELogicEvent.Arrange);

        // COLoginReq(Prepare). Player 대신 LoginSession을 생성·등록한다.
        // ODLoginReq 송신은 같은 tick의 Work phase에서 LoginSession.Execute가 한다.
        RegisterHandler<COLoginReq>((int)EMessageID.CoLoginReq,
            OnCOLoginReq, ELogicEvent.Prepare);

        // DOLoginAck(Prepare). DB 인증 성공 응답이며 이 시점에 Player를 생성한다.
        // archive 변경은 tick 스레드에서만 해야 하므로 Work가 아닌 Prepare에서 처리한다.
        RegisterHandler<DOLoginAck>((int)EMessageID.DoLoginAck,
            OnDOLoginAck, ELogicEvent.Prepare);

        // Player 단위 패킷(COGetPlayerReq 등)은 Player.Execute에서 처리한다. 등록은 Player의 static 테이블.
    }

    public override void Event(long tickMs)
    {
        if (_lastServerInfoSyncTime + ServerInfoSyncMs <= tickMs)
        {
            _lastServerInfoSyncTime = tickMs;
            UpdateServerInfo();
        }

        if (_lastBiCurrentUserSyncTime + BiCurrentUserSyncMs <= tickMs)
        {
            _lastBiCurrentUserSyncTime = tickMs;
            UpdateBICurrentUser();
        }

        if (_nextServerAliveSyncTime <= tickMs)
        {
            _nextServerAliveSyncTime = tickMs + ServerAliveSyncMs;
            SyncAlive();
        }

        if (_nextPlayerCountSyncTime <= tickMs)
        {
            _nextPlayerCountSyncTime = tickMs + PlayerCountSyncMs;
            UpdatePlayerCount();
        }

        if (_nextLoginTimeoutCheck <= tickMs)
        {
            _nextLoginTimeoutCheck = tickMs + LoginTimeoutCheckMs;
            RemoveExpiredLogins(tickMs, LoginTimeoutMs);
        }
    }

    // 타임아웃된 로그인 세션 정리. Event phase(단일 tick 스레드)에서만 호출한다.
    // 만료 세션은 archive에서 제거하고 네트워크 세션도 닫는다.
    private void RemoveExpiredLogins(long now, long timeoutMs)
    {
        if (_archive.Count == 0) return;

        List<long>? expired = null;
        foreach (var worker in _archive.Values)
        {
            if (worker is not LoginSession session) continue;
            if (now - session.CreatedAt <= timeoutMs) continue;
            (expired ??= new List<long>()).Add(session.SessionID);
        }

        if (expired is null) return;

        foreach (var sessionID in expired)
        {
            _archive.Remove(sessionID);
            NetworkManager.Instance.CloseSession(sessionID);
            Log.Warning("LoginSession timed out SessionID={ID}", sessionID);
        }
    }

    // Work phase. 병렬 실행은 PlayerWorkExecutor가 담당하고, 순회 대상(archive)은 여기서 넘긴다.
    public override void Work(long tickMs, Dictionary<long, List<PacketMessage>> sessionPackets)
        => _workExecutor.Run(tickMs, _archive.Values, sessionPackets);

    // ====================== 메시지 핸들러 ======================

    private void OnNetDisconnect(long sessionID, NetDisconnect msg, byte flag)
    {
        _ = msg;
        _ = flag;

        // Player면 제거를 미룬다. Player.OnNetDisconnect(Work)가 DB 세션 종료 저장(ODExitGameSessionReq)을 시작하고,
        // 완료 응답(DOExitGameSessionAck)의 Arrange에서 archive를 제거한다.
        if (_archive.Find<Player>(sessionID) is not null)
        {
            Log.Debug("Disconnect — defer Player removal until ExitGameSession ack SessionID={ID}", sessionID);
            return;
        }

        // LoginSession(로그인 미완료)이나 미등록 세션은 저장할 게임 세션이 없으므로 즉시 제거한다.
        if (_archive.Remove(sessionID))
            Log.Debug("Worker removed on disconnect SessionID={ID}", sessionID);
    }

    // 세션 종료 저장 완료 ack(Arrange). archive에서 Player를 제거하고 InGame에 OIExitGameSessionReq를 보내 필드 캐릭터를 정리한다.
    // Arrange는 Work 이후의 단일 tick 스레드라 Work의 Values 순회와 겹치지 않아 제거가 안전하다.
    private void OnDOExitGameSessionAck(long sessionID, DOExitGameSessionAck msg, byte flag)
    {
        _ = msg;
        _ = flag;

        if (_archive.Remove(sessionID))
            Log.Debug("Worker removed after ExitGameSession ack SessionID={ID}", sessionID);

        InGameService.Instance.EnqueuePacket(
            sessionID, (int)EMessageID.OiExitGameSessionReq, new OIExitGameSessionReq().ToByteArray());
    }

    private void OnAliveReq(long sessionID, NetAliveReq msg, byte flag)
    {
        _ = msg; // 현재 payload 사용 없음
        SendTo(sessionID, (int)EMessageID.NetAliveAck, new NetAliveAck());
    }

    // COLoginReq의 Prepare 처리 부분. LoginSession을 생성·등록하고 데이터 필드(PID 등)를 msg에서 채운다.
    // ODLoginReq 송신은 같은 tick의 Work phase에서 LoginSession.OnCOLoginReq가 한다.
    private void OnCOLoginReq(long sessionID, COLoginReq msg, byte flag)
    {
        // 인증 대기 중(LoginSession)이거나 이미 로그인된(Player) 세션의 중복 COLoginReq는 막는다.
        if (_archive.Find<LoginSession>(sessionID) is not null || _archive.Find<Player>(sessionID) is not null)
        {
            Log.Warning("COLoginReq duplicated SessionID={ID} PID={PID}", sessionID, msg.PID);
            return;
        }

        var login = new LoginSession(sessionID, TimeManager.Instance.TickMillis())
        {
            PID          = msg.PID,
            LoginVersion = msg.CurrentVersion,
            IsReconnect  = msg.IsReconnect,
            LanguageID   = msg.LanguageID,
        };

        if (!_archive.TryRegister(sessionID, login))
        {
            Log.Warning("COLoginReq register failed SessionID={ID} PID={PID}", sessionID, msg.PID);
            return;
        }

        Log.Information("COLoginReq accepted SessionID={ID} PID={PID}", sessionID, msg.PID);
    }

    // DOLoginAck의 Prepare 처리 부분. DB 인증이 성공한 이 시점에 Player를 생성한다.
    // LoginSession을 제거하고 그 자리에 Player를 등록한 뒤 클라이언트에 OCLoginAck로 응답한다.
    private void OnDOLoginAck(long sessionID, DOLoginAck msg, byte flag)
    {
        var login = _archive.Find<LoginSession>(sessionID);
        if (login is null)
        {
            // 타임아웃 등으로 이미 제거된 세션이면 무시한다.
            Log.Debug("DOLoginAck for unknown LoginSession SessionID={ID}", sessionID);
            return;
        }

        _archive.Remove(sessionID);

        var player = new Player(sessionID)
        {
            AccountID = msg.AccountID,
            PID       = msg.PID,
            State     = EPlayerState.LoggedIn,
        };

        if (!_archive.TryRegister(sessionID, player))
        {
            Log.Warning("DOLoginAck register failed SessionID={ID} PID={PID}", sessionID, msg.PID);
            return;
        }

        var ack = new OCLoginAck
        {
            MessageID               = EMessageID.OcLoginAck,
            AccountID               = msg.AccountID,
            AccountName             = msg.PlayerName,
            ConntectedIP            = string.Empty,
            ConnectedPort           = 0,
            IsReconnect             = msg.IsReconnect,
            IsNewAccount            = msg.IsNewAccount,
            FreeNicknameChangeCount = msg.FreeNicknameChangeCount,
            Version                 = login.LoginVersion.ToString(),
            ServerID                = 0,
            ChannelID               = msg.ChannelID,
        };
        SendTo(sessionID, (int)EMessageID.OcLoginAck, ack);

        Log.Information("Login ok SessionID={ID} PID={PID} AccountID={AID}", sessionID, player.PID, player.AccountID);
    }

    // ====================== 주기 작업 ======================

    private void UpdateServerInfo()
    {
        // TODO: Redis 서버 정보 동기화
    }

    private void UpdateBICurrentUser()
    {
        // TODO: BI 동시접속자 수 기록
    }

    private void SyncAlive()
    {
        // TODO: 마스터 서버에 alive heartbeat 송신
    }

    private void UpdatePlayerCount()
    {
        // TODO: 외부 시스템 동기화용 플레이어 수 업데이트
    }
}
