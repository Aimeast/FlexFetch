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
    private readonly StartupTasksHostedService _startup;

    public PotSupervisorHostedService(
        PotProviderService pot,
        StartupTasksHostedService startup,
        IHostApplicationLifetime lifetime,
        ILogger log)
        : base(lifetime, log, "PotSupervisor")
    {
        _pot = pot;
        _startup = startup;
    }

    protected override bool RunImmediately => true;

    protected override TimeSpan GetInterval() => Interval;

    protected override async Task ExecuteOnceAsync(CancellationToken cancellationToken)
    {
        // The first round may install missing pieces (deno, bgutil) - wait
        // for the startup component plan so it precedes any install line.
        await _startup.WhenComponentPlanLogged.WaitAsync(cancellationToken);
        await _pot.EnsureRunningAsync(cancellationToken);
    }
}
