using FlexFetch.Config;
using FlexFetch.Data;
using FlexFetch.Services;
using FlexFetch.Services.Downloaders;
using FlexFetch.Services.Tasks;

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
            IConfigRepository config,
            YtdlpService ytdlp,
            StealthBrowserService browser) =>
        {
            var dataDir = config.Get(ConfigKeys.DataDir) ?? ConfigRegistry.GetDefault(ConfigKeys.DataDir);
            var disk = GetDiskInfo(dataDir);
            return Results.Ok(new
            {
                version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
                startedAt = Program.StartedAt,
                diskFreeBytes = disk.FreeBytes,
                diskTotalBytes = disk.TotalBytes,
                concurrencyLimit = tasks.ConcurrencyLimit,
                runningTasks = tasks.RunningCount,
                queuedTasks = tasks.QueuedCount,
                ytdlpVersion = ytdlp.GetVersionAsync().GetAwaiter().GetResult(),
                browser = browser.SystemBrowserPath ?? "not-detected",
                browserSelfCheck = browser.IsRunning ? browser.SelfCheckAsync().GetAwaiter().GetResult() : Array.Empty<DetectionCheckResult>(),
            });
        });

        var admin = app.MapGroup("/api/system").RequireAuthorization("Admin");

        admin.MapPost("/shutdown", (IHostApplicationLifetime lifetime) =>
        {
            _ = Task.Run(() => lifetime.StopApplication());
            return Results.Ok();
        });

        admin.MapPost("/upgrade", async (YtdlpService ytdlp) =>
        {
            await ytdlp.UpgradeYtDlpAsync();
            return Results.Ok(new { version = await ytdlp.GetVersionAsync() });
        });
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
