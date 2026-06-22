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

    // Player 단위 worker phase 오케스트레이션 + live Player 집합 소유 — 이 eventor 가 소유(composition).
    private readonly PlayerWorkExecutor _workExecutor = new();

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

        // CALoginReq 는 한 tick 안에서 두 phase 에 걸쳐 처리된다:
        //  Prepare(여기 OnCALoginReq) — Player 를 인증 대기(AuthPending) 상태로 생성·등록만 한다.
        //  Work        — 같은 tick 에서 Player.OnCALoginReq 가 ADLoginReq 를 Data 계층으로 송신한다.
        RegisterHandler<CALoginReq>((int)EMessageID.CaLoginReq,
            OnCALoginReq, ELogicEvent.Prepare);

        // Player 단위 패킷(CAGetPlayerReq 등)은 Player.Execute 안에서 처리 — 등록은 Player static 테이블.
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

    // worker phase — Player 단위 병렬 분산은 PlayerWorkExecutor 가 담당. 여기선 위임만 한다.
    public override void Work(long tickMs, Dictionary<long, List<PacketMessage>> sessionPackets)
        => _workExecutor.Run(tickMs, sessionPackets);

    // ====================== 메시지 핸들러 ======================

    private void OnNetDisconnect(long sessionId, NetDisconnect msg, byte flag)
    {
        if (IsPrepareEvent(flag))
        {
            if (_workExecutor.Remove(sessionId))
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

    // CALoginReq 의 Prepare phase 담당부 — 여기선 Player 등록까지만 하고,
    // 실제 로그인 요청(ADLoginReq) 송신은 같은 tick 의 Work phase 에서 Player.OnCALoginReq 가 수행한다.
    private void OnCALoginReq(long sessionId, CALoginReq msg, byte flag)
    {
        // 멱등성: 동일 세션에서 중복 CALoginReq 차단.
        if (_workExecutor.Find(sessionId) is not null)
        {
            Log.Warning("CALoginReq duplicated SessionId={Id} Pid={Pid}", sessionId, msg.Pid);
            return;
        }

        // AccountId 는 Data 계층의 DALoginAck 로 채워진다 — 인증 대기 상태로 등록.
        var player = new Player(sessionId)
        {
            AccountId = 0,
            Pid       = msg.Pid,
            State     = EPlayerState.AuthPending,
        };

        if (!_workExecutor.TryRegister(player))
        {
            Log.Warning("CALoginReq register failed SessionId={Id} Pid={Pid}", sessionId, msg.Pid);
            return;
        }

        Log.Information("CALoginReq accepted SessionId={Id} Pid={Pid}", sessionId, msg.Pid);
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
