using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using TH.Common.Config;
using TH.Common.Time;
using TH.Server.Logging;

namespace TH.Server;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var builder = Host.CreateApplicationBuilder(args);

            // 시간 공급자: 로깅 설정에서도 쓰이므로 먼저 인스턴스화하여 DI에도 같은 객체 등록
            var timeProvider = new SystemTimeProvider();
            builder.Services.AddSingleton<ITimeProvider>(timeProvider);

            // ── Logging ──
            LoggerSetup.ConfigureSerilog(builder, timeProvider);

            // ── Config ──
            var configDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "config"));
            var configManager = new ConfigManager(configDir);
            builder.Services.AddSingleton<IConfigManager>(configManager);

            // ── Infrastructure ──
            builder.Services.AddHostedService<GameServerHost>();

            builder.Build().Run();
            return 0;
        }
        catch (Exception ex)
        {
            // Serilog가 떠 있다면 Log.Fatal로, 아니면 Console fallback
            Log.Fatal(ex, "Host terminated unexpectedly");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}