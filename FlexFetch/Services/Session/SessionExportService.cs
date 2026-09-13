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
    SessionHealth Health,
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
public sealed class SessionExportService
{
    private readonly FirefoxBrowserService _browser;
    private readonly SessionSnapshotService _snapshot;
    private readonly SessionProbeService _probe;
    private readonly IConfiguration _config;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _exportLock = new(1, 1);
    private readonly Random _random = new();

    private DateTime? _lastReExportRequestUtc;

    public SessionExportService(
        FirefoxBrowserService browser,
        SessionSnapshotService snapshot,
        SessionProbeService probe,
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
    public bool IsRunning => _exportLock.CurrentCount == 0;

    public DateTime? LastRunAt { get; private set; }

    public SessionHealth LastResult { get; private set; } = SessionHealth.Unknown;

    public string? LastError { get; private set; }

    /// <summary>Pipeline stage currently executing (for the status API).</summary>
    public string CurrentStage { get; private set; } = "idle";

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
    public async Task<SessionHealth> RunExportAsync(CancellationToken cancellationToken = default)
    {
        if (!await _exportLock.WaitAsync(0, cancellationToken))
        {
            throw new InvalidOperationException("A session export is already running");
        }

        try
        {
            var result = await RunExportCoreAsync(cancellationToken);
            LastResult = result;
            LastError = null;
            return result;
        }
        catch (Exception ex)
        {
            LastResult = SessionHealth.Error;
            LastError = ex.Message;
            throw;
        }
        finally
        {
            LastRunAt = DateTime.UtcNow;
            CurrentStage = "idle";
            _exportLock.Release();
        }
    }

    private async Task<SessionHealth> RunExportCoreAsync(CancellationToken cancellationToken)
    {
        if (!_snapshot.Exists)
        {
            throw new InvalidOperationException("No session snapshot exists; import a session first");
        }

        var seed = _snapshot.ReadCookies();
        var meta = _snapshot.ReadMeta() ?? new SessionMeta();
        var health = SessionHealth.Ok;

        CurrentStage = "launch";
        await _browser.EnsureSessionAsync();

        CurrentStage = "seed";
        await _browser.SeedCookiesAsync(seed);

        // Silent jar read: no site page is loaded here.
        CurrentStage = "jar";
        var jar = SessionSnapshotService.FilterYouTubeDomain(await _browser.ReadJarAsync());

        CurrentStage = "identity";
        var missing = MissingIdentityCookies(jar);
        if (missing.Count > 0)
        {
            // The browser round-trip lost the identity: the profile does not
            // hold a logged-in session. Reject - never overwrite the snapshot
            // with an anonymous jar.
            throw new ExportRejectedException(
                SessionHealth.LoginRequired,
                $"Identity cookies missing from the browser jar: {string.Join(", ", missing)}");
        }

        // Authoritative gate: probe a candidate jar with a one-time copy.
        CurrentStage = "probe";
        var visitorData = ExtractVisitorData(jar);
        var candidatePath = WriteCandidateJar(jar);
        try
        {
            var probe = await _probe.ProbeAsync(
                _probe.GetProbeUrl(_config),
                candidatePath,
                YouTubePosture.BuildExtractorArgs(YouTubePosture.CookieClients, visitorData),
                cancellationToken);
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
            CurrentStage = "humanize";
            var webRejection = await HumanizedBridgeVisitAsync(cancellationToken);
            if (webRejection is not null)
            {
                health = SessionHealth.WebBindingOnly;
                _log.Information("Session export: web surface rejected the session ({Reason}); InnerTube accepted it, exporting anyway", webRejection);
            }

            // Re-read the jar after the visit: the server may have rotated
            // tickets. Only use the post-visit jar when it is still intact.
            var refreshed = SessionSnapshotService.FilterYouTubeDomain(await _browser.ReadJarAsync());
            if (MissingIdentityCookies(refreshed).Count == 0 && refreshed.Count > 0)
            {
                jar = refreshed;
                visitorData = ExtractVisitorData(jar) ?? visitorData;
            }
        }

        CurrentStage = "write";
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
    public async Task<SessionImportResult> ImportAsync(string text, Uri? url, CancellationToken cancellationToken = default)
    {
        if (!await _exportLock.WaitAsync(0, cancellationToken))
        {
            throw new InvalidOperationException("A session export/import is already running");
        }

        try
        {
            var parse = CookieTextParser.Parse(text, url);
            if (parse.Cookies.Count == 0)
            {
                return new SessionImportResult(false, SessionHealth.Error,
                    $"No cookies recognized: {string.Join("; ", parse.Errors)}", 0, 0, CurrentCount());
            }

            var filtered = SessionSnapshotService.FilterYouTubeDomain(parse.Cookies);
            if (filtered.Count == 0)
            {
                return new SessionImportResult(false, SessionHealth.Error,
                    "No youtube.com cookies in the import - only the youtube.com domain is imported", parse.Cookies.Count, 0, CurrentCount());
            }

            var merged = SessionSnapshotService.Merge(_snapshot.ReadCookies(), filtered);
            var visitorData = ExtractVisitorData(merged);
            var candidatePath = WriteCandidateJar(merged);
            try
            {
                var probe = await _probe.ProbeAsync(
                    _probe.GetProbeUrl(_config),
                    candidatePath,
                    YouTubePosture.BuildExtractorArgs(YouTubePosture.CookieClients, visitorData),
                    cancellationToken);

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

            var meta = _snapshot.ReadMeta() ?? new SessionMeta();
            meta.ImportedAt = DateTime.UtcNow;
            meta.Health = SessionHealth.Ok.ToString();
            meta.VisitorData = visitorData;
            meta.LastError = null;
            _snapshot.WriteSnapshot(merged, meta);
            _log.Information("Session import accepted: {Imported} imported, {Total} total in snapshot",
                filtered.Count, merged.Count);
            return new SessionImportResult(true, SessionHealth.Ok, null, parse.Cookies.Count, filtered.Count, merged.Count);
        }
        finally
        {
            LastRunAt = DateTime.UtcNow;
            CurrentStage = "idle";
            _exportLock.Release();
        }
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
        var dir = Path.Combine(Path.GetDirectoryName(_snapshot.SnapshotPath)!, "copies");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"candidate-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, CookieFile.BuildContent(cookies));
        return path;
    }

    private int CurrentCount() => _snapshot.ReadCookies().Count;

    private static string Describe(ProbeResult probe)
    {
        var lines = probe.Output
            .Where(l => l.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase)
                || l.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        return lines.Count > 0 ? string.Join(" | ", lines) : $"exit={probe.RunSucceeded}";
    }

    /// <summary>Maps a probe classification onto a session health verdict.</summary>
    public static SessionHealth MapProbeClass(YtdlpOutputClass c) => c switch
    {
        YtdlpOutputClass.Ok => SessionHealth.Ok,
        YtdlpOutputClass.BotCheck => SessionHealth.BotCheck,
        YtdlpOutputClass.SessionRotated => SessionHealth.SessionRotated,
        YtdlpOutputClass.JarInconsistent => SessionHealth.JarInconsistent,
        YtdlpOutputClass.LoginRequired => SessionHealth.LoginRequired,
        YtdlpOutputClass.ClientBlocked => SessionHealth.Unknown,
        _ => SessionHealth.Error,
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
    public ExportRejectedException(SessionHealth health, string message) : base(message)
    {
        Health = health;
    }

    public SessionHealth Health { get; }
}
