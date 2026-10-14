using FlexFetch.Config;
using FlexFetch.Entities;
using FlexFetch.Services.Downloaders;
using Microsoft.Playwright;
using Serilog;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Services.Session;

/// <summary>Result of a session import attempt.</summary>
public sealed record SessionImportResult(
    bool Ok,
    YouTubeSessionHealth Health,
    string? Error,
    int Parsed,
    int ImportedToSnapshot,
    int TotalInSnapshot);

/// <summary>
/// The session export pipeline - the ONLY writer of the snapshot. Each run
/// re-seeds the Firefox profile from the current snapshot, reads the jar
/// back silently, checks the identity family, gates on the InnerTube probe,
/// optionally bridges via a humanized site visit, and writes the refreshed
/// jar back (filtered to the import domain, keeping the snapshot minimal).
/// Any failed step rejects the export and keeps the last known good
/// snapshot. All cross-process waits are bounded and the whole pipeline is
/// serialized by one lock (periodic export, canary re-export and manual
/// triggers share it).
/// </summary>
public sealed class YouTubeSessionExportService
{
    private readonly FirefoxBrowserService _browser;
    private readonly YouTubeSessionStore _snapshot;
    private readonly YouTubeSessionProbeService _probe;
    private readonly IConfiguration _config;
    private readonly ILogger _log;
    private readonly SessionPipeline _pipeline = new("A session export/import is already running");
    private readonly Random _random = new();

    private DateTime? _lastReExportRequestUtc;

    public YouTubeSessionExportService(
        FirefoxBrowserService browser,
        YouTubeSessionStore snapshot,
        YouTubeSessionProbeService probe,
        IConfiguration config,
        ILogger log)
    {
        _browser = browser;
        _snapshot = snapshot;
        _probe = probe;
        _config = config;
        _log = log;
    }

    /// <summary>True while an export/import pipeline holds the lock.</summary>
    public bool IsRunning => _pipeline.IsRunning;

    public DateTime? LastRunAt => _pipeline.LastRunAt;

    public YouTubeSessionHealth LastResult { get; private set; } = YouTubeSessionHealth.Unknown;

    public string? LastError => _pipeline.LastError;

    /// <summary>Pipeline stage currently executing (for the status API).</summary>
    public string CurrentStage => _pipeline.CurrentStage;

    /// <summary>Latest live line from the running probe, e.g. yt-dlp's own
    /// "Downloading webpage" or the PO token generation (status API surface;
    /// null while no pipeline runs).</summary>
    public string? CurrentDetail => _pipeline.CurrentDetail;

    /// <summary>
    /// Identity family a logged-in YouTube jar must contain (all of them);
    /// missing entries mean the jar is anonymous - exporting it would
    /// overwrite a good snapshot with a logged-out one.
    /// </summary>
    public static readonly string[] IdentityCookieNames =
    {
        "LOGIN_INFO", "SAPISID", "APISID", "__Secure-1PAPISID", "__Secure-3PAPISID",
    };

    /// <summary>Lists identity-family names missing from a jar (empty = ok).</summary>
    public static IReadOnlyList<string> MissingIdentityCookies(IReadOnlyList<CookieItem> jar)
    {
        var names = jar.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        return IdentityCookieNames.Where(n => !names.Contains(n)).ToList();
    }

    /// <summary>Extracts the visitor identity (VISITOR_INFO1_LIVE cookie value).</summary>
    public static string? ExtractVisitorData(IReadOnlyList<CookieItem> jar) =>
        jar.FirstOrDefault(c => c.Name.Equals("VISITOR_INFO1_LIVE", StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>
    /// Runs the full export pipeline. Throws InvalidOperationException when
    /// another export is already running; throws TimeoutException /
    /// ExportRejectedException for bounded failures (the snapshot stays
    /// untouched in every failure path).
    /// </summary>
    public async Task<YouTubeSessionHealth> RunExportAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _pipeline.RunAsync(async ct =>
            {
                var result = await RunExportCoreAsync(ct);
                LastResult = result;
                return result;
            }, cancellationToken);
        }
        catch (Exception)
        {
            // The pipeline records the error text; the verdict mapping onto
            // the YouTube health enum stays here.
            LastResult = YouTubeSessionHealth.Error;
            throw;
        }
    }

    private async Task<YouTubeSessionHealth> RunExportCoreAsync(CancellationToken cancellationToken)
    {
        if (!_snapshot.Exists)
        {
            throw new InvalidOperationException("No session snapshot exists; import a session first");
        }

        // Hold the browser busy across the whole pipeline: stages between
        // browser operations (the InnerTube probe runs yt-dlp for minutes)
        // must not let the idle sweeper close the browser mid-export.
        using var browserHold = _browser.HoldSessionUse();

        var seed = _snapshot.ReadCookies();
        var meta = _snapshot.ReadMeta() ?? new YouTubeSessionMeta();
        var health = YouTubeSessionHealth.Ok;

        _pipeline.SetStage("launch");
        await _browser.EnsureSessionAsync();

        _pipeline.SetStage("seed");
        await _browser.SeedCookiesAsync(seed);

        // Silent jar read: no site page is loaded here.
        _pipeline.SetStage("jar");
        var jar = YouTubeSessionStore.FilterYouTubeDomain(await _browser.ReadJarAsync());

        _pipeline.SetStage("identity");
        var missing = MissingIdentityCookies(jar);
        if (missing.Count > 0)
        {
            // The browser round-trip lost the identity: the profile does not
            // hold a logged-in session. Reject - never overwrite the snapshot
            // with an anonymous jar.
            throw new ExportRejectedException(
                YouTubeSessionHealth.LoginRequired,
                $"Identity cookies missing from the browser jar: {string.Join(", ", missing)}");
        }

        // Authoritative gate: probe a candidate jar with a one-time copy.
        _pipeline.SetStage("probe");
        _pipeline.SetDetail(null);
        var visitorData = ExtractVisitorData(jar);
        var candidatePath = WriteCandidateJar(jar);
        try
        {
            var probe = await _probe.ProbeAsync(
                _probe.GetProbeUrl(_config),
                candidatePath,
                YouTubePosture.BuildExtractorArgs(YouTubePosture.CookieClients, visitorData),
                cancellationToken,
                progress: ProbeProgress());
            meta.LastProbeClass = probe.Class.ToString();
            if (!probe.Healthy)
            {
                throw new ExportRejectedException(
                    MapProbeClass(probe.Class),
                    $"Probe rejected the candidate jar: {Describe(probe)}");
            }
        }
        finally
        {
            TryDelete(candidatePath);
        }

        // Humanized identity refresh (config-gated): a light bridge visit so
        // short-lived tickets get rotated by the server. A web-side rejection
        // alone is informational (surface asymmetry): only InnerTube rejects
        // mean unhealthy, and InnerTube already accepted above.
        if (bool.TryParse(Get(ConfigKeys.SessionHumanize), out var humanize) && humanize)
        {
            _pipeline.SetStage("humanize");
            var webRejection = await HumanizedBridgeVisitAsync(cancellationToken);
            if (webRejection is not null)
            {
                health = YouTubeSessionHealth.WebBindingOnly;
                _log.Information("Session export: web surface rejected the session ({Reason}); InnerTube accepted it, exporting anyway", webRejection);
            }

            // Re-read the jar after the visit: the server may have rotated
            // tickets. Only use the post-visit jar when it is still intact.
            var refreshed = YouTubeSessionStore.FilterYouTubeDomain(await _browser.ReadJarAsync());
            if (MissingIdentityCookies(refreshed).Count == 0 && refreshed.Count > 0)
            {
                jar = refreshed;
                visitorData = ExtractVisitorData(jar) ?? visitorData;
            }
        }

        _pipeline.SetStage("write");
        meta.ExportedAt = DateTime.UtcNow;
        meta.VisitorData = visitorData;
        meta.Health = health.ToString();
        meta.LastError = null;
        _snapshot.WriteSnapshot(jar, meta);
        _log.Information("Session export completed: {Count} cookies, health={Health}", jar.Count, health);
        return health;
    }

    /// <summary>
    /// Imports a pasted jar (Netscape, browser JSON export or Set-Cookie):
    /// parse, filter to the import domain, merge by (name, domain) over the
    /// current snapshot, build a candidate and gate on the probe - all with
    /// ZERO browser navigation. The snapshot is only replaced when the probe
    /// accepts; otherwise the last known good snapshot is kept.
    /// </summary>
    public Task<SessionImportResult> ImportAsync(string text, Uri? url, CancellationToken cancellationToken = default) =>
        _pipeline.RunAsync(ct => ImportCoreAsync(text, url, ct), cancellationToken);

    private async Task<SessionImportResult> ImportCoreAsync(string text, Uri? url, CancellationToken cancellationToken)
    {
        _pipeline.SetStage("parse");
        var parse = CookieTextParser.Parse(text, url);
        if (parse.Cookies.Count == 0)
        {
            return new SessionImportResult(false, YouTubeSessionHealth.Error,
                $"No cookies recognized: {string.Join("; ", parse.Errors)}", 0, 0, CurrentCount());
        }

        var filtered = YouTubeSessionStore.FilterYouTubeDomain(parse.Cookies);
        if (filtered.Count == 0)
        {
            return new SessionImportResult(false, YouTubeSessionHealth.Error,
                "No youtube.com cookies in the import - only the youtube.com domain is imported", parse.Cookies.Count, 0, CurrentCount());
        }

        var merged = YouTubeSessionStore.Merge(_snapshot.ReadCookies(), filtered);
        var visitorData = ExtractVisitorData(merged);
        var candidatePath = WriteCandidateJar(merged);
        try
        {
            _pipeline.SetStage("probe");
            _pipeline.SetDetail(null);
            var probe = await _probe.ProbeAsync(
                _probe.GetProbeUrl(_config),
                candidatePath,
                YouTubePosture.BuildExtractorArgs(YouTubePosture.CookieClients, visitorData),
                cancellationToken,
                progress: ProbeProgress());

            if (!probe.Healthy)
            {
                var health = MapProbeClass(probe.Class);
                _snapshot.UpdateMeta(m =>
                {
                    m.Health = health.ToString();
                    m.LastProbeClass = probe.Class.ToString();
                    m.LastError = $"Import rejected: {Describe(probe)}";
                });
                return new SessionImportResult(false, health,
                    $"Import rejected by probe ({probe.Class}): {Describe(probe)}",
                    parse.Cookies.Count, 0, CurrentCount());
            }
        }
        finally
        {
            TryDelete(candidatePath);
        }

        var meta = _snapshot.ReadMeta() ?? new YouTubeSessionMeta();
        meta.ImportedAt = DateTime.UtcNow;
        meta.Health = YouTubeSessionHealth.Ok.ToString();
        meta.VisitorData = visitorData;
        meta.LastError = null;
        _pipeline.SetStage("write");
        _snapshot.WriteSnapshot(merged, meta);
        _log.Information("Session import accepted: {Imported} imported, {Total} total in snapshot",
            filtered.Count, merged.Count);
        return new SessionImportResult(true, YouTubeSessionHealth.Ok, null, parse.Cookies.Count, filtered.Count, merged.Count);
    }

    /// <summary>
    /// Fire-and-forget re-export used by the downloader when yt-dlp reports a
    /// rotated session. Throttled: at most one request per throttle window;
    /// when an export is already running the request is dropped (the running
    /// export refreshes anyway).
    /// </summary>
    public void TriggerReExport()
    {
        var throttleHours = double.TryParse(Get(ConfigKeys.SessionCanaryReexportThrottleHours), out var h) && h > 0 ? h : 1;
        var now = DateTime.UtcNow;
        if (_lastReExportRequestUtc is { } last && (now - last).TotalHours < throttleHours)
        {
            _log.Debug("Re-export throttled (last request {Minutes:F0} min ago)", (now - last).TotalMinutes);
            return;
        }

        _lastReExportRequestUtc = now;
        _log.Information("Session re-export triggered by rotated-session report");
        _ = Task.Run(async () =>
        {
            try
            {
                await RunExportAsync();
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Triggered session re-export failed");
            }
        });
    }

    /// <summary>
    /// Light humanized bridge visit: one YouTube landing with dwell and a
    /// random scroll so server-side ticket rotation happens, then signal
    /// checks. Bounded; never throws on navigation failures (the probe is the
    /// authority, this visit only rotates tickets and collects web signals).
    /// </summary>
    private async Task<string?> HumanizedBridgeVisitAsync(CancellationToken cancellationToken)
    {
        var page = await _browser.NewSessionPageAsync();
        try
        {
            await page.GotoAsync("https://www.youtube.com/", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = (float)FirefoxBrowserService.PageReadTimeout.TotalMilliseconds * 5,
            });
            await page.WaitForTimeoutAsync(_random.Next(3000, 8000));
            await page.Mouse.WheelAsync(0, _random.Next(200, 800));
            await page.WaitForTimeoutAsync(_random.Next(1500, 4000));

            if (YouTubeDownloader.IsLoginRedirectUrl(page.Url))
            {
                return $"landing URL is a login/verification page ({page.Url})";
            }

            var loggedIn = await page.EvaluateAsync<bool?>(
                "() => { try { const d = window.ytcfg && window.ytcfg.data_; return (d && typeof d.loggedIn === 'boolean') ? d.loggedIn : null; } catch { return null; } }");
            return loggedIn == false ? "page reports logged in=false" : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warning("Humanized bridge visit failed (non-fatal): {Message}", ex.Message);
            return null;
        }
        finally
        {
            try
            {
                await page.CloseAsync().WaitAsync(FirefoxBrowserService.PageCloseTimeout);
            }
            catch
            {
                // Bounded close: a stuck page must not hang the pipeline.
            }
        }
    }

    private string WriteCandidateJar(IReadOnlyList<CookieItem> cookies)
    {
        var dir = _snapshot.CopyDirectory;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"candidate-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, CookieFile.BuildContent(cookies));
        return path;
    }

    /// <summary>Wires the probe's live output lines into <see cref="CurrentDetail"/>.</summary>
    private IProgress<string> ProbeProgress() => new Progress<string>(line => _pipeline.SetDetail(line));

    private int CurrentCount() => _snapshot.ReadCookies().Count;

    /// <summary>
    /// Builds the user-facing probe output excerpt. ERROR lines lead because
    /// they carry the exit cause - a failed run can trail its decisive ERROR
    /// behind several benign-looking WARNINGs (e.g. a missing JS runtime
    /// surfaces as warnings while "requested format is not available" is what
    /// actually failed the probe); warnings follow for context.
    /// </summary>
    public static string Describe(ProbeResult probe)
    {
        var errors = probe.Output
            .Where(l => l.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
            .Take(2);
        var warnings = probe.Output
            .Where(l => l.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase))
            .Take(2);
        var lines = errors.Concat(warnings).Take(3).ToList();
        return lines.Count > 0 ? string.Join(" | ", lines) : $"exit={probe.RunSucceeded}";
    }

    /// <summary>Maps a probe classification onto a session health verdict.</summary>
    public static YouTubeSessionHealth MapProbeClass(YtdlpOutputClass c) => c switch
    {
        YtdlpOutputClass.Ok => YouTubeSessionHealth.Ok,
        YtdlpOutputClass.BotCheck => YouTubeSessionHealth.BotCheck,
        YtdlpOutputClass.SessionRotated => YouTubeSessionHealth.SessionRotated,
        YtdlpOutputClass.JarInconsistent => YouTubeSessionHealth.JarInconsistent,
        YtdlpOutputClass.LoginRequired => YouTubeSessionHealth.LoginRequired,
        YtdlpOutputClass.ClientBlocked => YouTubeSessionHealth.Unknown,
        _ => YouTubeSessionHealth.Error,
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A leftover copy is cleaned by the next run; never fail the
            // pipeline for cleanup.
        }
    }

    private string Get(string key) => ConfigRegistry.From(_config, key);
}

/// <summary>The pipeline rejected the candidate jar (snapshot untouched).</summary>
public sealed class ExportRejectedException : Exception
{
    public ExportRejectedException(YouTubeSessionHealth health, string message) : base(message)
    {
        Health = health;
    }

    public YouTubeSessionHealth Health { get; }
}
