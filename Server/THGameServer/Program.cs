using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TH.Common.Time;
using TH.Server.GameServerHost;
using TH.Server.Logging;

var builder = Host.CreateApplicationBuilder(args);

// 시간 공급자: 로깅 설정에서도 쓰이므로 먼저 인스턴스화하여 DI에도 같은 객체 등록
var timeProvider = new SystemTimeProvider();
builder.Services.AddSingleton<ITimeProvider>(timeProvider);

// ── Logging ──
LoggerSetup.ConfigureSerilog(builder, timeProvider);

// ── Infrastructure ──
builder.Services.AddHostedService<GameServerHost>();

await builder.Build().RunAsync();
