using Serilog;

namespace FlexFetch.Startup;

/// <summary>Serilog bootstrap: structured logging to console and rolling files.</summary>
public static class LoggingSetup
{
    public static void AddFlexFetchLogging(this WebApplicationBuilder builder, string dataDir)
    {
        // Structured logging to console and rolling files (file output can be
        // disabled via Logging:WriteToFile, e.g. in tests).
        var loggerConfig = new LoggerConfiguration()
            .ReadFrom.Configuration(builder.Configuration)
            .Enrich.FromLogContext()
            .WriteTo.Console();
        if (builder.Configuration.GetValue("Logging:WriteToFile", true))
        {
            loggerConfig = loggerConfig.WriteTo.File(
                path: Path.Combine(dataDir, "logs", "flexfetch-.log"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 100 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
        }

        Log.Logger = loggerConfig.CreateLogger();

        builder.Host.UseSerilog();
    }
}
