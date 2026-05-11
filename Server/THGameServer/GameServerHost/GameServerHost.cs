using Microsoft.Extensions.Hosting;

namespace TH.Server.GameServerHost;

public sealed class GameServerHost : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // config load(설정 로드)
        // socket start(소켓 시작)
        // main loop(메인 루프)

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
}
