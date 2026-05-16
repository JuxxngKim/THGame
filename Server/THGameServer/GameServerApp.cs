using Serilog;
using TH.Common.Config;
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
            var configDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "config"));
            if (!ConfigManager.Instance.Init(configDir))
                return false;

            LoggerSetup.Init(SystemTimeProvider.Instance);

            Log.Information("GameServer 시작 (Env={Env}, Id={Id})",
                ConfigManager.Instance.Env, ConfigManager.Instance.Id);

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

        ConfigManager.Instance.Shutdown();
        Log.CloseAndFlush();
    }
}
