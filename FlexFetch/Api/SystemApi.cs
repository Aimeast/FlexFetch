using FlexFetch.Config;
using FlexFetch.Services;
using FlexFetch.Services.Downloaders;
using FlexFetch.Services.Session;
using FlexFetch.Services.Tasks;
using Microsoft.Playwright;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Api;

/// <summary>
/// System information, graceful shutdown and on-demand component upgrade.
/// </summary>
public static class SystemApi
{
    public static void Map(WebApplication app)
    {
        var info = app.MapGroup("/api/system").RequireAuthorization();

        info.MapGet("/info", (
            TaskService tasks,
            IConfiguration config,
            YtdlpService ytdlp) =>
        {
            var dataDir = ConfigRegistry.From(config, ConfigKeys.DataDir);
            var disk = GetDiskInfo(dataDir);
            return Results.Ok(new
            {
                version = BuildInfo.Version,
                gitLog = BuildInfo.GitLog,
                buildDateTime = BuildInfo.BuildDateTime,
                buildConfiguration = BuildInfo.Configuration,
                startedAt = Program.StartedAt,
                diskFreeBytes = disk.FreeBytes,
                diskTotalBytes = disk.TotalBytes,
                concurrencyLimit = tasks.ConcurrencyLimit,
                runningTasks = tasks.RunningCount,
                queuedTasks = tasks.QueuedCount,
                ytdlpVersion = ytdlp.GetVersionAsync().GetAwaiter().GetResult(),
                denoVersion = ytdlp.GetDenoVersionAsync().GetAwaiter().GetResult(),
                ffmpegVersion = ytdlp.GetFfmpegVersionAsync().GetAwaiter().GetResult(),
                playwrightVersion = typeof(Playwright).Assembly.GetName().Version?.ToString() ?? "unknown",
                firefox = FirefoxBrowserService.FindFirefoxExecutable() ?? "not-detected",
            });
        });

        var admin = app.MapGroup("/api/system").RequireAuthorization("Admin");

        admin.MapPost("/shutdown", (IHostApplicationLifetime lifetime) =>
        {
            // Mark the stop as API-initiated so the stop log can distinguish
            // it from Ctrl+C / host signals.
            Program.ShutdownByApi = true;
            _ = Task.Run(() => lifetime.StopApplication());
            return Results.Ok(new { shuttingDown = true });
        });

        admin.MapPost("/upgrade", (YtdlpService ytdlp, ILogger log) =>
        {
            if (ytdlp.IsUpgrading)
            {
                return Results.Conflict(new { error = "An upgrade is already in progress" });
            }

            // Run in the background so the page can poll /upgrade/status and
            // show live progress; the response returns immediately.
            _ = Task.Run(async () =>
            {
                try
                {
                    await ytdlp.UpgradeAllAsync();
                }
                catch (Exception ex)
                {
                    log.Warning(ex, "Component upgrade failed");
                }
            });
            return Results.Accepted((string?)null, new { upgrading = true });
        });

        admin.MapGet("/upgrade/status", (YtdlpService ytdlp) =>
            Results.Ok(new
            {
                upgrading = ytdlp.IsUpgrading,
                component = ytdlp.CurrentUpgradeComponent,
                lastError = ytdlp.LastUpgradeError,
            }));
    }

    private static (long FreeBytes, long TotalBytes) GetDiskInfo(string path)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path)) ?? path);
            return (drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch
        {
            return (0, 0);
        }
    }
}
