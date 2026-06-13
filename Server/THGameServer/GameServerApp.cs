using Serilog;
using Th;
using TH.Common.Config;
using TH.Common.Network;
using TH.Common.Time;
using TH.Server.Data;
using TH.Server.Logging;
using TH.Server.Logic;

namespace TH.Server;

public sealed class GameServerApp
{
    private volatile bool _shutdown;
    private volatile bool _exited;

    public bool Start()
    {
        try
        {
            _ = TimeManager.Instance;

            var configDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "config"));
            if (!ConfigManager.Instance.Init(configDir))
                return false;

            LoggerSetup.Init(TimeManager.Instance);

            Log.Information("GameServer started (Env={Env}, Id={Id})",
                ConfigManager.Instance.Env, ConfigManager.Instance.Id);

            // OutGameService 초기화 시점에 OutGameLogicEventor 가 생성되며 핸들러가 모두 등록된다.
            OutGameService.Instance.Init();

            // Data(DB) 계층 — AD* 요청을 받아 DA* 로 응답. worker(샤드) 스레드 기동.
            DBService.Instance.Init();

            var section  = $"Game.{ConfigManager.Instance.Id}";
            var bindAddr = ConfigManager.Instance.GetRequired(section, "BindAddr");
            if (!NetworkHelper.TryParseEndPoint(bindAddr, out var endPoint) || endPoint is null)
            {
                Log.Error("BindAddr parse failed: {Addr}", bindAddr);
                return false;
            }

            if (!NetworkManager.Instance.Init(endPoint))
                return false;

            // Listener.ProcessAcceptResult는 OnSessionConnected 발화 후 BeginReceive를 호출하므로
            // OnPacketReceived 설정 시점에는 아직 수신이 시작되지 않았다 (race 없음).
            NetworkManager.Instance.OnSessionConnected += session =>
            {
                session.OnPacketReceived = (s, packetId, payload) =>
                {
                    // payload는 ReadOnlySpan<byte> 슬라이스 — 즉시 ToArray로 복사 (Span 캡처 금지).
                    PacketQueue.Instance.Enqueue(s.SessionId, packetId, payload.ToArray());
                };
            };

            // 세션 종료를 NetDisconnect 합성 패킷으로 변환해 PacketQueue 에 주입.
            // → tick 스레드의 Eventor.Prepare / Arrange phase 가 일관된 흐름으로 정리.
            NetworkManager.Instance.OnSessionDisconnected += session =>
            {
                PacketQueue.Instance.Enqueue(
                    session.SessionId, (int)EMessageID.NetDisconnect, Array.Empty<byte>());
            };

            return true;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "GameServerApp.Start failed");
            return false;
        }
    }

    public void Run()
    {
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _shutdown = true;
        };

        Log.Debug("Main loop running");

        while (!_shutdown)
        {
            Thread.Sleep(1);
        }

        Exit();
    }

    public void Exit()
    {
        if (_exited)
            return;
        _exited = true;

        Log.Information("GameServer shutdown");

        NetworkManager.Instance.Shutdown();
        OutGameService.Instance.Shutdown();
        DBService.Instance.Shutdown();
        ConfigManager.Instance.Shutdown();
        Log.CloseAndFlush();
    }
}
