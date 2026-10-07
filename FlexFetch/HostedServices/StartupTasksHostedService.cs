using FlexFetch.Config;
using FlexFetch.Services;
using FlexFetch.Services.Downloaders;
using FlexFetch.Services.Session;
using FlexFetch.Services.Tasks;
using Serilog;
using ILogger = Serilog.ILogger;

namespace FlexFetch.HostedServices;

/// <summary>
/// Runs one-shot startup tasks once the host has finished starting
/// (Kestrel is listening): recovers pending tasks and installs missing
/// components (gated by ops.autoInstallDeps). The service completes after
/// running, so it is never a periodic worker.
/// </summary>
public sealed class StartupTasksHostedService : BackgroundService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IConfiguration _config;
    private readonly UserService _userService;
    private readonly IConfiguration _appConfig;
    private readonly IWebHostEnvironment _environment;
    private readonly TaskService _taskService;
    private readonly YtdlpService _ytdlp;
    private readonly FirefoxBrowserService _browser;
    private readonly ILogger _log;

    /// <summary>Completed once the component plan (ready / to-install) has
    /// been logged. Services that can trigger installs on their own first
    /// round await it, so the plan always precedes any install log line.</summary>
    private readonly TaskCompletionSource _componentPlanLogged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WhenComponentPlanLogged => _componentPlanLogged.Task;

    private void SignalComponentPlanLogged() => _componentPlanLogged.TrySetResult();

    public StartupTasksHostedService(
        IHostApplicationLifetime lifetime,
        IConfiguration config,
        UserService userService,
        IConfiguration appConfig,
        IWebHostEnvironment environment,
        TaskService taskService,
        YtdlpService ytdlp,
        FirefoxBrowserService browser,
        ILogger log)
    {
        _lifetime = lifetime;
        _config = config;
        _userService = userService;
        _appConfig = appConfig;
        _environment = environment;
        _taskService = taskService;
        _ytdlp = ytdlp;
        _browser = browser;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield so StartAsync returns immediately; then wait until the host
        // has finished starting, so startup tasks never compete with it.
        await Task.Yield();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = _lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        try
        {
            await started.Task.WaitAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return; // shutting down before startup finished
        }

        // 1. Create the initial admin when the database has no users yet
        //    (admin is always the first account).
        var initialAdminPassword = _userService.EnsureInitialAdmin(
            _appConfig["Admin:InitialPassword"], _environment.IsDevelopment());
        if (initialAdminPassword is not null)
        {
            if (_environment.IsDevelopment())
            {
                _log.Information("Created initial admin 'admin'. Password: {Password}", initialAdminPassword);
            }
            else
            {
                // Production: the initial password goes only to the console -
                // never into the file log. A dedicated console-only logger
                // keeps this one message out of the file sink.
                var consoleOnly = new LoggerConfiguration()
                    .MinimumLevel.Information()
                    .WriteTo.Console()
                    .CreateLogger();
                consoleOnly.Information("Created initial admin 'admin'. Password: {Password}", initialAdminPassword);
                _log.Information("Created initial admin 'admin'. Password shown on console only.");
            }
        }

        // 2. Component check: one line for ready components (skipped), one
        //    for components that need installing; then install only the
        //    missing ones so each install task logs its own progress.
        var ready = new List<string>();
        var toInstall = new List<string>();
        if (_ytdlp.IsYtDlpInstalled()) ready.Add("yt-dlp"); else toInstall.Add("yt-dlp");
        if (_ytdlp.IsDenoInstalled()) ready.Add("deno"); else toInstall.Add("deno");
        if (_ytdlp.IsFfmpegInstalled()) ready.Add("ffmpeg"); else toInstall.Add("ffmpeg");
        // Firefox readiness covers the three layers that can drift apart:
        // the browser binary (persistent volume), the apt-installed OS
        // libraries (ephemeral container filesystem) and the build number
        // the running Playwright package pins (changes on a package bump) -
        // each has its own check/marker so any mismatch triggers a reinstall.
        if (_browser.IsFirefoxReady()) ready.Add("firefox"); else toInstall.Add("firefox");

        if (ready.Count > 0)
        {
            _log.Information("Components ready, skipping install: {Components}", string.Join(", ", ready));
        }

        if (toInstall.Count > 0 && (!bool.TryParse(Get(ConfigKeys.AutoInstallDeps), out var autoInstall) || !autoInstall))
        {
            _log.Information("Components missing and auto-install disabled: {Components}", string.Join(", ", toInstall));
            SignalComponentPlanLogged();
            return;
        }

        if (toInstall.Count > 0)
        {
            _log.Information("Installing missing components: {Components}", string.Join(", ", toInstall));
        }

        // Signal BEFORE any install work starts: the pot supervisor and the
        // session maintenance can both trigger installs on their first round -
        // they wait for this signal, so the plan above always precedes every
        // individual install line no matter which service wins the race.
        SignalComponentPlanLogged();

        if (toInstall.Count > 0)
        {
            try
            {
                // yt-dlp/deno/ffmpeg: only missing ones are installed (installed are silent).
                if (!_ytdlp.IsYtDlpInstalled() || !_ytdlp.IsDenoInstalled() || !_ytdlp.IsFfmpegInstalled())
                {
                    await _ytdlp.EnsureInstalledAsync(stoppingToken);
                }

                // Firefox: the session source browser (Playwright build);
                // EnsureInstalledAsync is a fast no-op when the browser is
                // fully ready (executable, OS libraries, pinned build).
                if (!_browser.IsFirefoxReady())
                {
                    await _browser.EnsureInstalledAsync(stoppingToken);
                }

                _log.Information("All components ready: {Components}",
                    string.Join(", ", ready.Concat(toInstall)));
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Startup dependency install failed");
            }
        }

        // 3. Recover tasks after a restart (Running/Queued -> Queued, re-queued).
        var recovered = _taskService.RecoverPending();
        _log.Information("Recovered {Count} pending tasks after restart", recovered);

        // 4. Adopt on-disk file names for completed tasks whose recorded
        // name/size mismatch (e.g. garbled by a download-report encoding
        // mismatch) - the disk file is the source of truth.
        var reconciled = _taskService.ReconcileFileNames();
        _log.Information("Reconciled {Count} completed task file names from disk", reconciled);
    }

    private string Get(string key) => ConfigRegistry.From(_config, key);
}
