using FlexFetch.Services.Session;
using ILogger = Serilog.ILogger;

namespace FlexFetch.HostedServices;

/// <summary>
/// PO token provider caretaker: on a short fixed cadence it installs any
/// missing piece (Node, server package, yt-dlp plugin), starts the token
/// server and revives it after a crash. All failures are logged and retried
/// on the next round.
/// </summary>
public sealed class PotSupervisorHostedService : IntervalHostedService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(3);

    private readonly PotProviderService _pot;

    public PotSupervisorHostedService(
        PotProviderService pot,
        IHostApplicationLifetime lifetime,
        ILogger log)
        : base(lifetime, log, "PotSupervisor")
    {
        _pot = pot;
    }

    protected override bool RunImmediately => true;

    protected override TimeSpan GetInterval() => Interval;

    protected override async Task ExecuteOnceAsync(CancellationToken cancellationToken)
    {
        await _pot.EnsureRunningAsync(cancellationToken);
    }
}
