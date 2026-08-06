using FlexFetch.Config;
using FlexFetch.Data;
using FlexFetch.Services;
using ILogger = Serilog.ILogger;

namespace FlexFetch.HostedServices;

/// <summary>
/// Periodically upgrades external components (yt-dlp) to the latest version,
/// through the proxy policy. Gated by ops.autoUpgradeEnabled.
/// </summary>
public sealed class ComponentUpgradeHostedService : IntervalHostedService
{
    private readonly IConfigRepository _config;
    private readonly YoutubeDLService _ytdlp;

    public ComponentUpgradeHostedService(IConfigRepository config, YoutubeDLService ytdlp, ILogger log)
        : base(log, "ComponentUpgrade")
    {
        _config = config;
        _ytdlp = ytdlp;
    }

    protected override bool RunImmediately => false;

    protected override TimeSpan GetInterval()
    {
        var hours = int.TryParse(Get(ConfigKeys.UpgradeHours), out var h) && h > 0 ? h : 24;
        return TimeSpan.FromHours(hours);
    }

    protected override async Task ExecuteOnceAsync(CancellationToken cancellationToken)
    {
        if (!bool.TryParse(Get(ConfigKeys.AutoUpgrade), out var enabled) || !enabled)
        {
            return;
        }

        await _ytdlp.UpgradeAsync(cancellationToken);
    }

    private string Get(string key) => _config.Get(key) ?? ConfigRegistry.GetDefault(key);
}
