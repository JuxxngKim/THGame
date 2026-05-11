using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using TH.Common.Time;

namespace TH.Server.Logging;

internal static class LoggerSetup
{
    private const string OutputTemplate =
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    public static void ConfigureSerilog(HostApplicationBuilder builder, ITimeProvider time)
    {
        var startKst = time.NowKst();
        var dateFolder = $"{startKst.Year:D4}{startKst.Month:D2}{startKst.Day:D2}";
        var timeStamp  = $"{startKst.Year:D4}{startKst.Month:D2}{startKst.Day:D2}-" +
                         $"{startKst.Hour:D2}{startKst.Minute:D2}{startKst.Second:D2}";

        var logDir = Path.Combine(AppContext.BaseDirectory, "log", dateFolder);
        Directory.CreateDirectory(logDir);

        var logPath = Path.Combine(logDir, $"GameServer-{timeStamp}.log");

#if DEBUG
        var minLevel = LogEventLevel.Debug;
#else
        var minLevel = LogEventLevel.Information;
#endif

        builder.Services.AddSerilog((sp, lc) => lc
            .MinimumLevel.Is(minLevel)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: OutputTemplate)
            .WriteTo.File(
                path: logPath,
                outputTemplate: OutputTemplate,
                shared: false,
                flushToDiskInterval: TimeSpan.FromSeconds(1)));
    }
}
