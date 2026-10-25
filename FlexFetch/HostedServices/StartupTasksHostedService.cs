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
/// <summary>Inputs of the one-shot startup sequence; a record keeps the
/// static, testable entry point readable instead of a ten-parameter list.</summary>
public sealed record StartupSequenceContext(
    Serilog.ILogger Log,
    CancellationToken StoppingToken,
    Func<int> RecoverPending,
    Func<int> ReconcileFileSizes,
    Func<int> ReleaseWaitingTasks,
    IReadOnlyList<string> ReadyComponents,
    IReadOnlyList<string> MissingComponents,
    bool AutoInstallEnabled,
    Func<Task> InstallMissingComponents,
    Action SignalComponentPlanLogged);

public sealed class StartupTasksHostedService : BackgroundService
{
    /// <summary>Runs the whole startup sequence with the pieces injected, so
    /// tests can pin the ordering that matters: interrupted downloads are
    /// recovered (marked Waiting) up front - a restart must leave no phantom
    /// Running tasks behind - but only released into the queue AFTER the
    /// component installs, so a download never starts against a missing
    /// yt-dlp or browser. Installs can take minutes (or hang on a broken
    /// proxy - a stuck Firefox download once kept recovered tasks invisible
    /// in Running for the whole container lifetime); the Waiting state makes
    /// that window honest in the UI instead.</summary>
    public static async Task RunStartupSequenceAsync(StartupSequenceContext context)
    {
        var log = context.Log;
        var stoppingToken = context.StoppingToken;
        var recoverPending = context.RecoverPending;
        var reconcileFileSizes = context.ReconcileFileSizes;
        var releaseWaitingTasks = context.ReleaseWaitingTasks;
        var readyComponents = context.ReadyComponents;
        var missingComponents = context.MissingComponents;
        var autoInstallEnabled = context.AutoInstallEnabled;
        var installMissingComponents = context.InstallMissingComponents;
        var signalComponentPlanLogged = context.SignalComponentPlanLogged;

        // 1. Recovery marks orphans Waiting: visible immediately, executed
        //    only by the release at the end of the sequence.
        var recovered = recoverPending();
        log.Information("Recovered {Count} pending tasks after restart", recovered);

        // 2. Adopt on-disk file sizes for completed tasks whose recorded
        //    size mismatches (e.g. lost in a download-report encoding
        //    mismatch) - the disk file is the source of truth.
        var reconciled = reconcileFileSizes();
        log.Information("Reconciled {Count} completed task file sizes from disk", reconciled);

        // 3. Component plan: one line for ready components (skipped), one
        //    for components that need installing.
        if (readyComponents.Count > 0)
        {
            log.Information("Components ready, skipping install: {Components}",
                string.Join(", ", readyComponents));
        }

        if (missingComponents.Count > 0 && !autoInstallEnabled)
        {
            log.Information("Components missing and auto-install disabled: {Components}",
                string.Join(", ", missingComponents));
            signalComponentPlanLogged();
            // Nothing will ever install, so holding the tasks would strand
            // them in Waiting forever; release them to surface the real
            // download failures instead.
            releaseWaitingTasks();
            return;
        }

        if (missingComponents.Count > 0)
        {
            log.Information("Installing missing components: {Components}",
                string.Join(", ", missingComponents));
        }

        // Signal BEFORE any install work starts: the pot supervisor and the
        // session maintenance can both trigger installs on their own first
        // round - they wait for this signal, so the plan above always
        // precedes every individual install line no matter which service
        // wins the race.
        signalComponentPlanLogged();

        if (missingComponents.Count == 0)
        {
            releaseWaitingTasks();
            return;
        }

        // 4. Installs run last and only miss what is missing. A failure is
        //    logged, never thrown: the waiting tasks are released below and
        //    the next supervisor round retries the install.
        try
        {
            await installMissingComponents();
            log.Information("All components ready: {Components}",
                string.Join(", ", readyComponents.Concat(missingComponents)));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            log.Information("Startup component install cancelled by shutdown");
            // Shutting down: held tasks stay Waiting and the next boot's
            // recovery picks them up again.
            return;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Startup dependency install failed");
        }

        // 5. Installs done (or failed): open the queue.
        releaseWaitingTasks();
    }
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IConfiguration _config;
    private readonly UserService _userService;
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
        IWebHostEnvironment environment,
        TaskService taskService,
        YtdlpService ytdlp,
        FirefoxBrowserService browser,
        ILogger log)
    {
        _lifetime = lifetime;
        _config = config;
        _userService = userService;
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

        // Surface invalid editable settings once at startup. Consumers fall
        // back to their defaults, but a typo should not stay invisible.
        foreach (var item in ConfigRegistry.All)
        {
            var problem = ConfigRegistry.Validate(item.Key, ConfigRegistry.From(_config, item.Key));
            if (problem is not null)
            {
                _log.Warning("Configuration {Key} is invalid: {Problem}", item.Key, problem);
            }
        }

        // 1. Create the initial admin when the database has no users yet
        //    (admin is always the first account).
        var initialAdminPassword = _userService.EnsureInitialAdmin(
            _config["Admin:InitialPassword"], _environment.IsDevelopment());
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

        // Component checks are fast file lookups; the plan they produce is
        // logged and the installs (if any) run after task recovery.
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

        await RunStartupSequenceAsync(new StartupSequenceContext(
            _log,
            stoppingToken,
            RecoverPending: () => _taskService.RecoverPending(),
            ReconcileFileSizes: () => _taskService.ReconcileFileSizes(),
            ReleaseWaitingTasks: () => _taskService.ReleaseWaitingTasks(),
            ReadyComponents: ready,
            MissingComponents: toInstall,
            AutoInstallEnabled: bool.TryParse(Get(ConfigKeys.AutoInstallDeps), out var autoInstall) && autoInstall,
            InstallMissingComponents: async () =>
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
            },
            SignalComponentPlanLogged: SignalComponentPlanLogged));
    }

    private string Get(string key) => ConfigRegistry.From(_config, key);
}
