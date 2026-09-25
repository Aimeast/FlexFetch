using FlexFetch.Config;
using FlexFetch.Services.Downloaders;
using ILogger = Serilog.ILogger;

namespace FlexFetch.HostedServices;

/// <summary>
/// Periodic component upgrade service: on a schedule it upgrades yt-dlp and
/// deno to the latest version (gated by ops.autoUpgradeEnabled). The initial
/// install of missing components lives in StartupTasksHostedService.
/// </summary>
public sealed class DependencyUpgradeHostedService : IntervalHostedService
{
    private readonly IConfiguration _config;
    private readonly YtdlpService _ytdlp;

    public DependencyUpgradeHostedService(
        IConfiguration config,
        YtdlpService ytdlp,
        IHostApplicationLifetime lifetime,
        ILogger log)
        : base(lifetime, log, "DependencyUpgrade")
    {
        _config = config;
        _ytdlp = ytdlp;
    }

    // Upgrades wait for the first interval instead of running immediately.
    protected override bool RunImmediately => false;

    protected override TimeSpan GetInterval()
    {
        var hours = int.TryParse(Get(ConfigKeys.UpgradeHours), out var h) && h > 0 ? h : 24;
        return TimeSpan.FromHours(hours);
    }

    protected override async Task ExecuteOnceAsync(CancellationToken cancellationToken)
    {
        if (!bool.TryParse(Get(ConfigKeys.AutoUpgrade), out var autoUpgrade) || !autoUpgrade)
        {
            return;
        }

        await _ytdlp.UpgradeYtDlpAsync(cancellationToken);
        await _ytdlp.UpgradeDenoAsync(cancellationToken);
        await _ytdlp.UpgradeFfmpegAsync(cancellationToken);
    }

    private string Get(string key) => ConfigRegistry.From(_config, key);
}
