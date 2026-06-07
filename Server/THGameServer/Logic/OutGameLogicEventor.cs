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

    private long _lastServerInfoSyncTime;
    private long _lastBiCurrentUserSyncTime;
    private long _nextPlayerCountSyncTime;
    private long _nextServerAliveSyncTime;

    // 세션 단위 Player 보관자 — 이 eventor 가 소유. tick 스레드에서만 접근.
    private readonly PlayerArchive _archive = new();

    public OutGameLogicEventor()
    {
        long now = TimeManager.Instance.UnixMillis();
        _lastServerInfoSyncTime    = now;
        _lastBiCurrentUserSyncTime = now;
        _nextPlayerCountSyncTime   = now + PlayerCountSyncMs;
        _nextServerAliveSyncTime   = now + ServerAliveSyncMs;

        // NetDisconnect: Prepare 와 Arrange 양쪽 phase 에서 분기 처리.
        RegisterHandler<NetDisconnect>((int)EMessageID.NetDisconnect,
            OnNetDisconnect, ELogicEvent.Prepare | ELogicEvent.Arrange);

        RegisterHandler<NetAliveReq>((int)EMessageID.NetAliveReq,
            OnAliveReq, ELogicEvent.Arrange);

        RegisterHandler<CALoginReq>((int)EMessageID.CaLoginReq,
            OnCALoginReq, ELogicEvent.Prepare);

        RegisterHandler<CAGetPlayerReq>((int)EMessageID.CaGetPlayerReq,
            OnCAGetPlayerReq, ELogicEvent.Arrange);
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
    }

    // ====================== 메시지 핸들러 ======================

    private void OnNetDisconnect(long sessionId, NetDisconnect msg, byte flag)
    {
        if (IsPrepareEvent(flag))
        {
            if (_archive.Remove(sessionId))
                Log.Debug("PlayerArchive removed SessionId={Id}", sessionId);
        }
        else if (IsArrangeEvent(flag))
        {
            // TODO: InField 단계 정리 (게임 상태 cleanup)
            Log.Debug("Session {Id} disconnect (Arrange)", sessionId);
        }
    }

    private void OnAliveReq(long sessionId, NetAliveReq msg, byte flag)
    {
        _ = msg; // 현재 payload 사용 없음
        SendTo(sessionId, (int)EMessageID.NetAliveAck, new NetAliveAck());
    }

    private void OnCALoginReq(long sessionId, CALoginReq msg, byte flag)
    {
        // 멱등성: 동일 세션에서 중복 CALoginReq 차단.
        if (_archive.Find(sessionId) is not null)
        {
            Log.Warning("CALoginReq duplicated SessionId={Id} Pid={Pid}", sessionId, msg.Pid);
            return;
        }

        // AccountId 는 Data 서버에서 받아오는 값 — 동기 흐름에서는 0 으로 둠 (후속 PR 에서 채움).
        var player = new Player(sessionId)
        {
            AccountId = 0,
            Pid       = msg.Pid,
            State     = EPlayerState.LoggedIn,
        };

        if (!_archive.TryRegister(player))
        {
            Log.Warning("CALoginReq register failed SessionId={Id} Pid={Pid}", sessionId, msg.Pid);
            return;
        }

        // 동기 단순 흐름 — Data 서버 RPC 없이 즉시 ACK. 후속 PR 에서 비동기로 교체.
        var ack = new ACLoginAck
        {
            MessageID    = EMessageID.AcLoginAck,
            AccountId    = player.AccountId,
            IsNewAccount = false,
            Version      = msg.CurrentVersion.ToString(),
        };
        SendTo(sessionId, (int)EMessageID.AcLoginAck, ack);

        Log.Information("Login ok SessionId={Id} Pid={Pid}", sessionId, msg.Pid);
    }

    private void OnCAGetPlayerReq(long sessionId, CAGetPlayerReq msg, byte flag)
    {
        // TODO: PlayerArchive 조회 + AcGetPlayerAck 응답
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
