using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TH.Server.GameServerHost;

public sealed class GameServerHost : BackgroundService
{
    private readonly ILogger<GameServerHost> _logger;

    public GameServerHost(ILogger<GameServerHost> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("서버 시작");

        // config load(설정 로드)
        // socket start(소켓 시작)

        // main loop(메인 루프)
        _logger.LogDebug("메인 루프 대기 중");
        await Task.Delay(Timeout.Infinite, stoppingToken);

        _logger.LogInformation("서버 종료");
    }
}
