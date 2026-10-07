using FlexFetch.Services.Session;
using ILogger = Serilog.ILogger;

namespace FlexFetch.HostedServices;

/// <summary>
/// Idle sweeper for the browser stack: periodically asks the browser
/// service to release what idle time left - the session browser and the
/// Playwright driver (see FirefoxBrowserService.IdleCloseTimeout). The
/// export pipeline launches the browser on demand from the persistent
/// profile, so a released stack costs nothing but the next export's
/// launch time.
/// </summary>
public sealed class SessionBrowserIdleHostedService : IntervalHostedService
{
    private readonly FirefoxBrowserService _browser;

    public SessionBrowserIdleHostedService(
        FirefoxBrowserService browser,
        IHostApplicationLifetime lifetime,
        ILogger log)
        : base(lifetime, log, "SessionBrowserIdle")
    {
        _browser = browser;
    }

    /// <summary>One sweep per five minutes: the close is not latency
    /// sensitive, so the idle window may overshoot by up to this cadence.</summary>
    protected override TimeSpan GetInterval() => TimeSpan.FromMinutes(5);

    protected override Task ExecuteOnceAsync(CancellationToken cancellationToken)
        => _browser.CloseIfIdleAsync();
}
