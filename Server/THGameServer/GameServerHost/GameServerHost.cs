using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TH.Common.Config;

namespace TH.Server;

public sealed class GameServerHost : BackgroundService
{
    private readonly ILogger<GameServerHost> _logger;
    private readonly IConfigManager _config;

    public GameServerHost(ILogger<GameServerHost> logger, IConfigManager config)
    {
        _logger = logger;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("서버 시작 (Env={Env}, Id={Id})", _config.Env, _config.Id);

        // socket start(소켓 시작)

        // main loop(메인 루프)
        _logger.LogDebug("메인 루프 대기 중");
        await Task.Delay(Timeout.Infinite, stoppingToken);

        _logger.LogInformation("서버 종료");
    }
}
