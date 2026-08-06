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

    public CookieRefreshHostedService(
        IConfigRepository config,
        CookiePoolService pool,
        StealthBrowserService browser,
        ILogger log)
        : base(log, "CookieRefresh")
    {
        _config = config;
        _pool = pool;
        _browser = browser;
    }

    protected override bool RunImmediately => false;

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

        foreach (var group in _pool.GetGroups())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await _browser.RefreshGroupAsync(group);
        }
    }

    private string Get(string key) => _config.Get(key) ?? ConfigRegistry.GetDefault(key);
}
