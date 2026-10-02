using FlexFetch.Config;
using FlexFetch.Services.Session;
using ILogger = Serilog.ILogger;

namespace FlexFetch.HostedServices;

/// <summary>
/// Session maintenance, one periodic tick with two gears: while the
/// snapshot is young it runs the cheap health check (a minimal InnerTube
/// probe on a one-time snapshot copy); once the snapshot is older than the
/// period it runs the full export pipeline instead (Firefox re-seed, probe,
/// write-back), which refreshes the snapshot and resets the cycle. A
/// rotated/bot-checked verdict triggers a throttled re-export; N consecutive
/// unhealthy runs (default 3) raise an Error alert - at that point only a
/// human re-login and a fresh import can fix the session.
/// </summary>
public sealed class SessionMaintenanceHostedService : IntervalHostedService
{
    private readonly IConfiguration _config;
    private readonly SessionSnapshotService _snapshot;
    private readonly SessionProbeService _probe;
    private readonly SessionExportService _export;
    private readonly StartupTasksHostedService _startup;
    private readonly ILogger _log;

    public SessionMaintenanceHostedService(
        IConfiguration config,
        SessionSnapshotService snapshot,
        SessionProbeService probe,
        SessionExportService export,
        StartupTasksHostedService startup,
        IHostApplicationLifetime lifetime,
        ILogger log)
        : base(lifetime, log, "SessionMaintenance")
    {
        _config = config;
        _snapshot = snapshot;
        _probe = probe;
        _export = export;
        _startup = startup;
        _log = log;
    }

    protected override bool RunImmediately => true;

    /// <summary>Health-check bookkeeping (the export pipeline records its
    /// own verdicts via the snapshot meta and its own status).</summary>
    public DateTime? LastCheckAt { get; private set; }

    public SessionHealth LastHealth { get; private set; } = SessionHealth.Unknown;

    public string? LastDetail { get; private set; }

    public int ConsecutiveFailures { get; private set; }

    protected override TimeSpan GetInterval()
    {
        var hours = int.TryParse(Get(ConfigKeys.SessionPeriodHours), out var h) && h > 0 ? h : 12;
        return TimeSpan.FromHours(hours);
    }

    protected override async Task ExecuteOnceAsync(CancellationToken cancellationToken)
    {
        // A check or export can trigger component installs (probe, browser) -
        // wait for the startup component plan so it precedes any install line.
        await _startup.WhenComponentPlanLogged.WaitAsync(cancellationToken);

        // Importing a session is what opts the deployment in: with a
        // snapshot present maintenance runs, without one it idles.
        if (!_snapshot.Exists)
        {
            return;
        }

        if (_snapshot.Age is { } age && age >= GetInterval())
        {
            // Aged snapshot: the full pipeline refreshes it (and its probe
            // is the same authoritative check the light path would do).
            _log.Information("Periodic session export starting (snapshot age: {Age})", age);
            await _export.RunExportAsync(cancellationToken);
            return;
        }

        await RunHealthCheckAsync(cancellationToken);
    }

    /// <summary>
    /// Cheap health check: one InnerTube probe on a one-time snapshot copy,
    /// no browser involved.
    /// </summary>
    private async Task RunHealthCheckAsync(CancellationToken cancellationToken)
    {
        var copy = _snapshot.CreateSnapshotCopy();
        if (copy is null)
        {
            return;
        }

        try
        {
            var visitorData = _snapshot.ReadMeta()?.VisitorData;
            var probe = await _probe.ProbeAsync(
                Get(ConfigKeys.SessionProbeUrl),
                copy,
                YouTubePosture.BuildExtractorArgs(YouTubePosture.CookieClients, visitorData),
                cancellationToken);
            RecordVerdict(probe.Healthy ? SessionHealth.Ok : SessionExportService.MapProbeClass(probe.Class),
                string.Join(" | ", probe.Output.Take(2)));
        }
        catch (Exception ex)
        {
            RecordVerdict(SessionHealth.Error, ex.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(copy))
                {
                    File.Delete(copy);
                }
            }
            catch
            {
                // Cleanup is best-effort.
            }
        }
    }

    private void RecordVerdict(SessionHealth health, string? detail)
    {
        LastCheckAt = DateTime.UtcNow;
        LastHealth = health;
        LastDetail = detail;

        if (health == SessionHealth.Ok)
        {
            ConsecutiveFailures = 0;
            _snapshot.UpdateMeta(m => m.Health = health.ToString());
            _log.Information("Session health check: the session is healthy");
            return;
        }

        ConsecutiveFailures++;
        _snapshot.UpdateMeta(m =>
        {
            m.Health = health.ToString();
            m.LastError = detail;
        });

        _log.Warning("Session health check: unhealthy ({Health}){Detail}, consecutive failures: {Count}",
            health, detail is null ? string.Empty : $" - {detail}", ConsecutiveFailures);

        // Rotated/bot-checked jars may still be recoverable by a re-export;
        // an inconsistent jar can only be fixed by a fresh import.
        if (health is SessionHealth.BotCheck or SessionHealth.SessionRotated)
        {
            _export.TriggerReExport();
        }

        var threshold = int.TryParse(Get(ConfigKeys.SessionCanaryFailureThreshold), out var t) && t > 0 ? t : 3;
        if (ConsecutiveFailures >= threshold)
        {
            // Alert: the session is effectively dead. Only a human re-login
            // followed by a fresh import restores it.
            _log.Error(
                "SESSION ALERT: the YouTube session has been unhealthy for {Count} consecutive checks ({Health}). "
                + "Human action required: log in again in a real browser, export the youtube.com cookies and import them via POST /api/session/import.",
                ConsecutiveFailures, health);
        }
    }

    private string Get(string key) => ConfigRegistry.From(_config, key);
}
