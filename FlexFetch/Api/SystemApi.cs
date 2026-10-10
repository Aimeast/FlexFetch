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

        info.MapGet("/info", async (
            TaskService tasks,
            IConfiguration config,
            YtdlpService ytdlp,
            FirefoxBrowserService browser) =>
        {
            var dataDir = ConfigRegistry.From(config, ConfigKeys.DataDir);
            var disk = GetDiskInfo(dataDir);
            // Version probes run concurrently and are memoized in YtdlpService,
            // so only the first request after a start or upgrade pays for the
            // process spawns (the PyInstaller yt-dlp re-extracts on every run).
            var ytdlpVersion = ytdlp.GetVersionAsync();
            var denoVersion = ytdlp.GetDenoVersionAsync();
            var ffmpegVersion = ytdlp.GetFfmpegVersionAsync();
            await Task.WhenAll(ytdlpVersion, denoVersion, ffmpegVersion);

            // Readiness is what "all components installed" means for the
            // download paths (fast file/marker checks, no processes): the
            // versions above say WHAT is there, this says whether it all
            // works - notably the Firefox OS libraries, whose marker lives
            // in the container filesystem and is lost on every container
            // recreation (the browser binary on the data volume survives).
            var ytdlpReady = ytdlp.IsYtDlpInstalled();
            var denoReady = ytdlp.IsDenoInstalled();
            var ffmpegReady = ytdlp.IsFfmpegInstalled();
            var firefoxInstalled = browser.IsFirefoxInstalled();
            var firefoxOsDeps = browser.IsOsDepsInstalled();
            var firefoxBuildCurrent = FirefoxBrowserService.IsFirefoxBuildCurrent();
            var firefoxReady = firefoxInstalled && firefoxOsDeps && firefoxBuildCurrent;

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
                ytdlpVersion = await ytdlpVersion,
                denoVersion = await denoVersion,
                ffmpegVersion = await ffmpegVersion,
                playwrightVersion = typeof(Playwright).Assembly.GetName().Version?.ToString() ?? "unknown",
                firefox = FirefoxBrowserService.FindFirefoxExecutable() ?? "not-detected",
                components = new
                {
                    ytdlp = ytdlpReady,
                    deno = denoReady,
                    ffmpeg = ffmpegReady,
                    firefoxInstalled,
                    firefoxOsDeps,
                    firefoxBuildCurrent,
                    firefoxReady,
                },
                componentsReady = ytdlpReady && denoReady && ffmpegReady && firefoxReady,
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
