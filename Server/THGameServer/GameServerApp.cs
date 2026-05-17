using Serilog;
using TH.Common.Config;
using TH.Common.Network;
using TH.Common.Time;
using TH.Server.Logging;

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

            Log.Information("GameServer 시작 (Env={Env}, Id={Id})",
                ConfigManager.Instance.Env, ConfigManager.Instance.Id);

            var section  = $"Game.{ConfigManager.Instance.Id}";
            var bindAddr = ConfigManager.Instance.GetRequired(section, "BindAddr");
            if (!NetworkHelper.TryParseEndPoint(bindAddr, out var endPoint) || endPoint is null)
            {
                Log.Error("BindAddr 파싱 실패: {Addr}", bindAddr);
                return false;
            }

            if (!NetworkManager.Instance.Init(endPoint))
                return false;

            return true;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "GameServerApp.Start 실패");
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

        Log.Debug("메인 루프 대기 중");

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

        Log.Information("GameServer 종료");

        NetworkManager.Instance.Shutdown();
        ConfigManager.Instance.Shutdown();
        Log.CloseAndFlush();
    }
}
