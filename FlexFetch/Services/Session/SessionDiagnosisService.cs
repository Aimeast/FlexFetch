using FlexFetch.Config;

namespace FlexFetch.Services.Session;

/// <summary>One row of the diagnostic matrix.</summary>
public sealed record DiagnoseItem(
    string Mode,
    string Clients,
    bool RunSucceeded,
    string Class,
    bool Healthy,
    string Detail);

/// <summary>The complete result of one diagnostic matrix run.</summary>
public sealed record DiagnoseReport(
    DateTime RanAt,
    string ProbeUrl,
    IReadOnlyList<DiagnoseItem> Items,
    string Resolution);

/// <summary>
/// Runs the diagnostic matrix (client posture x anonymous/session) - the
/// first step of every session problem attribution - and keeps the last
/// report in memory, so the status page can show it by default and it
/// survives a page refresh (it does not survive a process restart).
/// </summary>
public sealed class SessionDiagnosisService
{
    /// <summary>Per-item timeout: tight enough for an HTTP request, generous
    /// enough for a slow proxy.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(90);

    private readonly SessionSnapshotService _snapshot;
    private readonly SessionProbeService _probe;
    private readonly IConfiguration _config;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Injectable probe runner (tests); defaults to the real probe.</summary>
    private readonly Func<string, string?, string, CancellationToken, Task<ProbeResult>> _runProbe;

    public SessionDiagnosisService(
        SessionSnapshotService snapshot,
        SessionProbeService probe,
        IConfiguration config,
        Serilog.ILogger log,
        Func<string, string?, string, CancellationToken, Task<ProbeResult>>? runProbe = null)
    {
        _snapshot = snapshot;
        _probe = probe;
        _config = config;
        _runProbe = runProbe ?? ((url, cookieFile, args, ct) => probe.ProbeAsync(url, cookieFile, args, ct, ProbeTimeout));
    }

    public DiagnoseReport? Last { get; private set; }

    /// <summary>True while a matrix run holds the gate.</summary>
    public bool IsRunning => _gate.CurrentCount == 0;

    /// <summary>
    /// Runs all matrix items concurrently and stores the report. Overlapping
    /// runs are rejected: each item is a real yt-dlp process, and a double
    /// click would hammer YouTube with duplicates.
    /// </summary>
    public async Task<DiagnoseReport> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!_gate.Wait(0))
        {
            throw new InvalidOperationException("A diagnostic matrix is already running");
        }

        try
        {
            var probeUrl = ConfigRegistry.From(_config, ConfigKeys.SessionProbeUrl);
            var visitorData = _snapshot.ReadMeta()?.VisitorData;
            var cookieArg = YouTubePosture.BuildExtractorArgs(YouTubePosture.CookieClients, visitorData);
            var anonymousArg = YouTubePosture.BuildExtractorArgs(YouTubePosture.AnonymousClients);

            // All items run concurrently: the wall time is bounded by ONE
            // probe timeout, not the sum of four.
            var anonymousTasks = new[]
            {
                ProbeItemAsync("anonymous", "mweb", null, cookieArg, probeUrl, cancellationToken),
                ProbeItemAsync("anonymous", "android_vr,mweb", null, anonymousArg, probeUrl, cancellationToken),
            };

            var copies = new List<string>();
            var sessionTasks = Task.WhenAll(Array.Empty<Task<DiagnoseItem>>());
            if (_snapshot.Exists)
            {
                // One COPY per session probe: yt-dlp rewrites the cookies
                // file it is given, so concurrent probes must not share.
                copies.Add(_snapshot.CreateSnapshotCopy()!);
                copies.Add(_snapshot.CreateSnapshotCopy()!);
                sessionTasks = Task.WhenAll(
                    ProbeItemAsync("session", "mweb", copies[0], cookieArg, probeUrl, cancellationToken),
                    ProbeItemAsync("session", "android_vr,mweb", copies[1], anonymousArg, probeUrl, cancellationToken));
            }

            var items = (await Task.WhenAll(anonymousTasks)).ToList();
            items.AddRange(await sessionTasks);

            foreach (var copy in copies)
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

            var report = new DiagnoseReport(DateTime.UtcNow, probeUrl, items, BuildResolution(items));
            Last = report;
            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<DiagnoseItem> ProbeItemAsync(
        string mode,
        string clients,
        string? cookieFile,
        string extractorArgs,
        string probeUrl,
        CancellationToken ct)
    {
        try
        {
            var result = await _runProbe(probeUrl, cookieFile, extractorArgs, ct);
            return new DiagnoseItem(mode, clients, result.RunSucceeded, result.Class.ToString(), result.Healthy,
                result.Output.FirstOrDefault(l => l.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)
                    || l.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase)) ?? result.VideoTitle);
        }
        catch (Exception ex)
        {
            return new DiagnoseItem(mode, clients, false, "Error", false, ex.Message);
        }
    }

    /// <summary>Derives the recommended next action from the matrix verdicts.</summary>
    public static string BuildResolution(IReadOnlyList<DiagnoseItem> items)
    {
        DiagnoseItem? Find(string mode, string clients) =>
            items.FirstOrDefault(i => i.Mode == mode && i.Clients == clients);

        var sessionMweb = Find("session", "mweb");
        var anonDefault = Find("anonymous", "android_vr,mweb");
        var anonMweb = Find("anonymous", "mweb");
        var sessionAnonPosture = Find("session", "android_vr,mweb");

        if (sessionMweb is { Healthy: true })
        {
            return sessionAnonPosture is { Healthy: true }
                ? "Session mode and anonymous mode both work; the session is healthy on the mweb client."
                : "Session mode works on mweb; keep cookie-authenticated runs on mweb only (android/tv with cookies triggers the bot wall).";
        }

        if (anonDefault is { Healthy: true })
        {
            return sessionMweb is null
                ? "Anonymous mode works and no session snapshot exists - no session needed for this content."
                : $"Anonymous mode works but the session is unusable ({sessionMweb?.Class}): re-export the session, or import a fresh jar if the rotation persists.";
        }

        if (anonMweb is { Healthy: true } && sessionMweb is null)
        {
            // The android_vr posture failed but plain mweb is enough for
            // anonymous downloads.
            return "Anonymous mweb works and no session snapshot exists - no session needed for this content.";
        }

        if (sessionMweb is { Healthy: false } && anonMweb is { Healthy: true })
        {
            return $"Anonymous mweb works while the session fails ({sessionMweb.Class}): the jar is likely rotated or inconsistent - import a fresh session.";
        }

        if (anonDefault is { Class: nameof(YtdlpOutputClass.BotCheck) } && sessionMweb is { Healthy: true })
        {
            return "Anonymous runs hit the bot wall but the session works - keep the session attached.";
        }

        return "Both anonymous and session probes failed - check the network/proxy first, then the probe URL, before touching the session.";
    }
}
