using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TH.Common.Time;
using TH.Server.GameServerHost;

var builder = Host.CreateApplicationBuilder(args);

// ── Infrastructure ──
builder.Services.AddSingleton<ITimeProvider, SystemTimeProvider>();
builder.Services.AddHostedService<GameServerHost>();

await builder.Build().RunAsync();
