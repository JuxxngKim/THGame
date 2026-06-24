using Google.Protobuf;
using Serilog;
using Th;
using TH.Common;
using TH.Common.Network;
using TH.Server.Game;
using TH.Common.Time;

namespace TH.Server.Logic;

// OutGame(로그인/세션 관리) 도메인 전용 Eventor.
// C++ OutGameLogicEventor 미러. Player / PlayerArchive 등 도메인 자료구조는 후속 PR.
public sealed class OutGameLogicEventor : LogicEventor
{
    private const long ServerInfoSyncMs    = 5_000;
    private const long BiCurrentUserSyncMs = 60_000;
    private const long ServerAliveSyncMs   = 1_000;
    private const long PlayerCountSyncMs   = 5_000;

    // 로그인 세션 타임아웃 — DOLoginAck 가 이 시간 내에 도착하지 않으면 세션을 정리한다.
    private const long LoginTimeoutMs        = 10_000;
    private const long LoginTimeoutCheckMs   = 1_000;

    private long _lastServerInfoSyncTime;
    private long _lastBiCurrentUserSyncTime;
    private long _nextPlayerCountSyncTime;
    private long _nextServerAliveSyncTime;
    private long _nextLoginTimeoutCheck;

    // Player 단위 worker phase 오케스트레이션 + live Player 집합 소유 — 이 eventor 가 소유(composition).
    private readonly PlayerWorkExecutor _workExecutor = new();

    public OutGameLogicEventor()
    {
        long now = TimeManager.Instance.UnixMillis();
        _lastServerInfoSyncTime    = now;
        _lastBiCurrentUserSyncTime = now;
        _nextPlayerCountSyncTime   = now + PlayerCountSyncMs;
        _nextServerAliveSyncTime   = now + ServerAliveSyncMs;
        // 로그인 타임아웃은 Event(tickMs) 의 monotonic 시간축(TickMillis)으로 비교하므로 같은 축으로 초기화.
        _nextLoginTimeoutCheck     = TimeManager.Instance.TickMillis() + LoginTimeoutCheckMs;

        // NetDisconnect: Prepare 와 Arrange 양쪽 phase 에서 분기 처리.
        RegisterHandler<NetDisconnect>((int)EMessageID.NetDisconnect,
            OnNetDisconnect, ELogicEvent.Prepare | ELogicEvent.Arrange);

        RegisterHandler<NetAliveReq>((int)EMessageID.NetAliveReq,
            OnAliveReq, ELogicEvent.Arrange);

        // COLoginReq(Prepare) — Player 대신 LoginSession 을 생성·등록한다. 실제 ODLoginReq 송신은
        // 같은 tick 의 Work phase 에서 LoginSession.Execute 가 수행한다.
        RegisterHandler<COLoginReq>((int)EMessageID.CoLoginReq,
            OnCOLoginReq, ELogicEvent.Prepare);

        // DOLoginAck(Prepare) — DB 인증 성공 응답. 이 시점에 비로소 Player 를 생성한다.
        // Player 생성(archive 변경)은 tick 스레드(Prepare)에서만 일어나야 하므로 Work 가 아닌 Prepare 에서 처리.
        RegisterHandler<DOLoginAck>((int)EMessageID.DoLoginAck,
            OnDOLoginAck, ELogicEvent.Prepare);

        // Player 단위 패킷(COGetPlayerReq 등)은 Player.Execute 안에서 처리 — 등록은 Player static 테이블.
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
            _workExecutor.RemoveExpiredLogins(tickMs, LoginTimeoutMs);
        }
    }

    // worker phase — Player 단위 병렬 분산은 PlayerWorkExecutor 가 담당. 여기선 위임만 한다.
    public override void Work(long tickMs, Dictionary<long, List<PacketMessage>> sessionPackets)
        => _workExecutor.Run(tickMs, sessionPackets);

    // ====================== 메시지 핸들러 ======================

    private void OnNetDisconnect(long sessionID, NetDisconnect msg, byte flag)
    {
        if (IsPrepareEvent(flag))
        {
            // 인증 완료 전(LoginSession) / 후(Player) 어느 단계든 끊길 수 있으므로 양쪽 모두 정리.
            if (_workExecutor.RemoveLogin(sessionID))
                Log.Debug("LoginSession removed SessionID={ID}", sessionID);
            if (_workExecutor.Remove(sessionID))
                Log.Debug("PlayerArchive removed SessionID={ID}", sessionID);
        }
        else if (IsArrangeEvent(flag))
        {
            // TODO: InField 단계 정리 (게임 상태 cleanup)
            Log.Debug("Session {ID} disconnect (Arrange)", sessionID);
        }
    }

    private void OnAliveReq(long sessionID, NetAliveReq msg, byte flag)
    {
        _ = msg; // 현재 payload 사용 없음
        SendTo(sessionID, (int)EMessageID.NetAliveAck, new NetAliveAck());
    }

    // COLoginReq 의 Prepare phase 담당부 — Player 가 아니라 LoginSession 을 생성·등록한다.
    // 실제 로그인 요청(ODLoginReq) 송신은 같은 tick 의 Work phase 에서 LoginSession.Execute 가 수행한다.
    private void OnCOLoginReq(long sessionID, COLoginReq msg, byte flag)
    {
        // 멱등성: 인증 대기 중(LoginSession)이거나 이미 로그인된(Player) 세션의 중복 COLoginReq 차단.
        if (_workExecutor.FindLogin(sessionID) is not null || _workExecutor.Find(sessionID) is not null)
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

        if (!_workExecutor.TryRegisterLogin(login))
        {
            Log.Warning("COLoginReq register failed SessionID={ID} PID={PID}", sessionID, msg.PID);
            return;
        }

        Log.Information("COLoginReq accepted SessionID={ID} PID={PID}", sessionID, msg.PID);
    }

    // DOLoginAck 의 Prepare phase 담당부 — DB 인증 성공 시 비로소 Player 를 생성한다.
    // LoginSession 을 제거하고 그 자리에 Player 를 등록한 뒤, 클라이언트에 OCLoginAck 로 응답한다.
    private void OnDOLoginAck(long sessionID, DOLoginAck msg, byte flag)
    {
        var login = _workExecutor.FindLogin(sessionID);
        if (login is null)
        {
            // 타임아웃 등으로 이미 제거된 세션 — 무시.
            Log.Debug("DOLoginAck for unknown LoginSession SessionID={ID}", sessionID);
            return;
        }

        _workExecutor.RemoveLogin(sessionID);

        var player = new Player(sessionID)
        {
            AccountID = msg.AccountID,
            PID       = msg.PID,
            State     = EPlayerState.LoggedIn,
        };

        if (!_workExecutor.TryRegister(player))
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
