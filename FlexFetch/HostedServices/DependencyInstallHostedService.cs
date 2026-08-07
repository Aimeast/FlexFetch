using System.Diagnostics;
using FlexFetch.Config;
using FlexFetch.Data;
using FlexFetch.Services;
using FlexFetch.Services.Downloaders;
using ILogger = Serilog.ILogger;

namespace FlexFetch.HostedServices;

/// <summary>
/// External component maintenance: on startup it installs whatever is
/// missing (yt-dlp, deno, headless browser — gated by ops.autoInstallDeps),
/// then on a schedule it upgrades yt-dlp and deno to the latest version
/// (gated by ops.autoUpgradeEnabled). The browser install logic lives in
/// StealthBrowserService (child process, proxy scoped to that process).
/// </summary>
public sealed class DependencyInstallHostedService : IntervalHostedService
{
    private readonly IConfigRepository _config;
    private readonly YtdlpService _ytdlp;
    private readonly StealthBrowserService _browser;
    private readonly ILogger _log;
    private bool _installChecked;

    public DependencyInstallHostedService(
        IConfigRepository config,
        YtdlpService ytdlp,
        StealthBrowserService browser,
        IHostApplicationLifetime lifetime,
        ILogger log)
        : base(lifetime, log, "DependencyInstall")
    {
        _config = config;
        _ytdlp = ytdlp;
        _browser = browser;
        _log = log;
    }

    protected override bool RunImmediately => true;

    protected override TimeSpan GetInterval()
    {
        var hours = int.TryParse(Get(ConfigKeys.UpgradeHours), out var h) && h > 0 ? h : 24;
        return TimeSpan.FromHours(hours);
    }

    protected override async Task ExecuteOnceAsync(CancellationToken cancellationToken)
    {
        // First run: install missing components synchronously so the service
        // is fully provisioned before the next upgrade cycle; failures are
        // caught inside RunInstallAsync and do not disturb the loop.
        if (!_installChecked)
        {
            _installChecked = true;
            if (bool.TryParse(Get(ConfigKeys.AutoInstallDeps), out var autoInstall) && autoInstall)
            {
                await RunInstallAsync(cancellationToken);
            }
        }

        // Every run: upgrade yt-dlp and deno to the latest version.
        if (bool.TryParse(Get(ConfigKeys.AutoUpgrade), out var autoUpgrade) && autoUpgrade)
        {
            await _ytdlp.UpgradeYtDlpAsync(cancellationToken);
            await _ytdlp.UpgradeDenoAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Installs missing components; failures are logged and do not disturb
    /// the application.
    /// </summary>
    private async Task RunInstallAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _ytdlp.EnsureInstalledAsync(cancellationToken);
            await _browser.EnsureBrowserInstalledAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Dependency install failed");
        }
    }

    private string Get(string key) => _config.Get(key) ?? ConfigRegistry.GetDefault(key);
}
