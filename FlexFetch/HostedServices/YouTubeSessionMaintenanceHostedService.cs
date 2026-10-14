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
public sealed class YouTubeSessionMaintenanceHostedService : IntervalHostedService
{
    private readonly IConfiguration _config;
    private readonly YouTubeSessionStore _snapshot;
    private readonly YouTubeSessionProbeService _probe;
    private readonly YouTubeSessionExportService _export;
    private readonly StartupTasksHostedService _startup;
    private readonly ILogger _log;

    public YouTubeSessionMaintenanceHostedService(
        IConfiguration config,
        YouTubeSessionStore snapshot,
        YouTubeSessionProbeService probe,
        YouTubeSessionExportService export,
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

    public YouTubeSessionHealth LastHealth { get; private set; } = YouTubeSessionHealth.Unknown;

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

        var action = SessionMaintenancePlan.Decide(_snapshot.Exists, _snapshot.Age, GetInterval());
        if (action == SessionMaintenanceAction.Idle)
        {
            // Importing a session is what opts the deployment in: with a
            // snapshot present maintenance runs, without one it idles.
            return;
        }

        if (action == SessionMaintenanceAction.FullRefresh)
        {
            // Aged snapshot: the full pipeline refreshes it (and its probe
            // is the same authoritative check the light path would do) - its
            // verdict feeds the same canary bookkeeping, so the periodic
            // card reflects it instead of staying "first check pending"
            // forever (with interval == snapshot period every round finds
            // the snapshot aged and the light path would never run).
            _log.Information("Periodic session export starting (snapshot age: {Age})", _snapshot.Age);
            await RunAgedExportRoundAsync(_log, _export.RunExportAsync, RecordExportVerdict, cancellationToken);
            return;
        }

        await RunHealthCheckAsync(cancellationToken);
    }

    /// <summary>
    /// One aged-snapshot round: run the full export pipeline and record its
    /// verdict in the canary bookkeeping. Static with injected delegates so
    /// tests can pin the verdict mapping without a browser; cancellation
    /// passes through untouched.
    /// </summary>
    public static async Task RunAgedExportRoundAsync(
        ILogger log,
        Func<CancellationToken, Task<YouTubeSessionHealth>> runExport,
        Action<YouTubeSessionHealth, string?> recordVerdict,
        CancellationToken cancellationToken)
    {
        try
        {
            var health = await runExport(cancellationToken);
            recordVerdict(health, null);
        }
        catch (ExportRejectedException ex)
        {
            recordVerdict(ex.Health, ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Periodic session export failed");
            recordVerdict(YouTubeSessionHealth.Error, ex.Message);
        }
    }

    /// <summary>True when the verdict still means the session works for
    /// yt-dlp. WebBindingOnly is informational - InnerTube accepted and only
    /// the web surface declined - so it counts as a pass for the failure
    /// counter instead of walking toward the alert.</summary>
    public static bool IsUsable(YouTubeSessionHealth health) =>
        health is YouTubeSessionHealth.Ok or YouTubeSessionHealth.WebBindingOnly;

    /// <summary>Canary bookkeeping for the aged-snapshot path: the export
    /// pipeline IS the refresh, so a rejection must not immediately trigger
    /// another one (TriggerReExport stays the light path's and the
    /// downloader's remedy; the next round retries on its own).</summary>
    private void RecordExportVerdict(YouTubeSessionHealth health, string? detail) =>
        RecordVerdict(health, detail, triggerReExport: false);

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
            RecordVerdict(probe.Healthy ? YouTubeSessionHealth.Ok : YouTubeSessionExportService.MapProbeClass(probe.Class),
                string.Join(" | ", probe.Output.Take(2)));
        }
        catch (Exception ex)
        {
            RecordVerdict(YouTubeSessionHealth.Error, ex.Message);
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

    private void RecordVerdict(YouTubeSessionHealth health, string? detail, bool triggerReExport = true)
    {
        LastCheckAt = DateTime.UtcNow;
        LastHealth = health;
        LastDetail = detail;

        if (IsUsable(health))
        {
            ConsecutiveFailures = 0;
            _snapshot.UpdateMeta(m => m.Health = health.ToString());
            if (health == YouTubeSessionHealth.Ok)
            {
                _log.Information("Session health check: the session is healthy");
            }
            else
            {
                _log.Information("Session health check: web surface declined but InnerTube accepted (informational)");
            }
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
        if (triggerReExport && health is YouTubeSessionHealth.BotCheck or YouTubeSessionHealth.SessionRotated)
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
