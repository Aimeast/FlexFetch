using FlexFetch.Config;
using FlexFetch.Data;
using FlexFetch.Services;
using ILogger = Serilog.ILogger;

namespace FlexFetch.HostedServices;

/// <summary>
/// Periodically refreshes cookie groups in the browser (humanized visits)
/// and writes the updated cookies back into the centralized pool. The
/// interval is randomized when cookie.refreshRandomize is enabled.
/// </summary>
public sealed class CookieRefreshHostedService : IntervalHostedService
{
    private readonly IConfigRepository _config;
    private readonly CookiePoolService _pool;
    private readonly StealthBrowserService _browser;
    private readonly ILogger _log;

    public CookieRefreshHostedService(
        IConfigRepository config,
        CookiePoolService pool,
        StealthBrowserService browser,
        IHostApplicationLifetime lifetime,
        ILogger log)
        : base(lifetime, log, "CookieRefresh")
    {
        _config = config;
        _pool = pool;
        _browser = browser;
        _log = log;
    }

    protected override bool RunImmediately =>
        bool.TryParse(Get(ConfigKeys.CookieRefreshOnStartup), out var onStartup) && onStartup;

    protected override TimeSpan GetInterval()
    {
        var hours = int.TryParse(Get(ConfigKeys.CookieRefreshHours), out var h) && h > 0 ? h : 24;
        if (bool.TryParse(Get(ConfigKeys.CookieRefreshRandomize), out var randomize) && randomize)
        {
            // Randomize within 50%..150% of the base period to avoid a fixed cadence.
            var factor = 0.5 + Random.Shared.NextDouble();
            return TimeSpan.FromHours(hours * factor);
        }
        return TimeSpan.FromHours(hours);
    }

    protected override async Task ExecuteOnceAsync(CancellationToken cancellationToken)
    {
        if (!bool.TryParse(Get(ConfigKeys.CookieAutoRefresh), out var enabled) || !enabled)
        {
            return;
        }

        var groups = _pool.GetGroups();
        _log.Information("Cookie refresh run started ({GroupCount} groups)", groups.Count);
        var refreshed = 0;
        foreach (var group in groups)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await _browser.RefreshGroupAsync(group);
                refreshed++;
            }
            catch (Exception ex)
            {
                // RefreshGroupAsync marks the group Failed and rethrows;
                // keep the periodic run going for the remaining groups.
                _log.Warning(ex, "Cookie refresh skipped group {Group}", group.Name);
            }
        }

        _log.Information("Cookie refresh run finished ({Refreshed}/{GroupCount} groups)", refreshed, groups.Count);
    }

    private string Get(string key) => _config.Get(key) ?? ConfigRegistry.GetDefault(key);
}
