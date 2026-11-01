using FlexFetch.Services.Routing;
using FlexFetch.Services.Session;
using Serilog;
using YoutubeDLSharp;
using YoutubeDLSharp.Metadata;
using YoutubeDLSharp.Options;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Services.Downloaders;

/// <summary>
/// YouTube downloader: yt-dlp with YouTube-specific URL matching and the
/// session-aware client posture. Public content runs anonymous first
/// (android_vr + mweb); when an auth-class failure comes back and a session
/// snapshot exists, the run is retried ONCE with a one-time snapshot copy on
/// the mweb client, paired with the jar's visitor identity. Cookie'd runs
/// never use android/tv clients - yt-dlp skips them and the leftover
/// posture triggers the bot wall.
/// </summary>
public sealed class YouTubeDownloader : YtdlpDownloader
{
    /// <summary>Fingerprint of the bgutil script-deno provider's version
    /// probe expiring (getpot_bgutil_script._GET_SCRIPT_VSN_TIMEOUT).</summary>
    private const string ProbeTimeoutSignature = "'--version']' timed out";

    private readonly YouTubeSessionStore _snapshot;
    private readonly YouTubeSessionExportService? _export;
    private readonly PotProviderService? _pot;

    public YouTubeDownloader(
        YtdlpService ytdlp,
        IProxyService proxy,
        StorageService storage,
        ILogger log,
        YouTubeSessionStore snapshot,
        YouTubeSessionExportService? export = null,
        PotProviderService? pot = null,
        Func<string, OptionSet, CancellationToken, Task<RunResult<VideoData>>>? fetchData = null,
        Func<string, OptionSet, Action<double>, Action<long>, CancellationToken, Task<RunResult<string>>>? download = null)
        : base(ytdlp, proxy, storage, log, fetchData, download)
    {
        _snapshot = snapshot;
        _export = export;
        _pot = pot;
    }

    public override string Type => "YouTube";

    public override bool IsDomainSpecific => true;

    /// <summary>YouTube enables the session attach-and-retry on auth errors.</summary>
    protected override bool SessionRetryEnabled => true;

    /// <summary>Builds the options with the posture matching the cookie
    /// state: anonymous runs use android_vr + mweb, cookie-carrying runs use
    /// mweb paired with the jar's visitor identity.</summary>
    public override OptionSet BuildOptions(Uri url, string? outputPath = null, string? cookieFile = null)
    {
        var options = base.BuildOptions(url, outputPath, cookieFile);
        var clients = cookieFile is null ? YouTubePosture.AnonymousClients : YouTubePosture.CookieClients;
        var visitorData = cookieFile is null ? null : _snapshot.ReadMeta()?.VisitorData;
        SetExtractorArgs(options,
            YouTubePosture.BuildExtractorArgs(clients, visitorData),
            _pot?.ScriptPathArg);
        return options;
    }

    /// <summary>Authenticated posture: mweb only, with the jar's visitor
    /// identity so session and visitor travel as a pair.</summary>
    protected override void ApplySessionPosture(OptionSet options, string cookieFile)
    {
        base.ApplySessionPosture(options, cookieFile);
        var visitorData = _snapshot.ReadMeta()?.VisitorData;
        SetExtractorArgs(options,
            YouTubePosture.BuildExtractorArgs(YouTubePosture.CookieClients, visitorData),
            _pot?.ScriptPathArg);
    }

    /// <summary>The plugin probes the PO token script with a hard timeout;
    /// when it expires inside yt-dlp, the run loses its PO token and the
    /// cookie'd mweb media URLs end in HTTP 403 on the video stream. Feed
    /// the signature back so the memoized deno warm-up is dropped and the
    /// next supervisor round recompiles.</summary>
    protected override void OnRunFailed(IReadOnlyList<string> errorOutput)
    {
        if (_pot is null
            || !errorOutput.Any(line => line.Contains(ProbeTimeoutSignature, StringComparison.Ordinal)))
        {
            return;
        }

        _pot.InvalidateWarmUp();
        Log.Warning(
            "The PO token script probe timed out inside yt-dlp; the deno warm-up was re-armed for the next supervisor round");
    }

    /// <summary>The cookie'd mweb posture needs a PO token: make sure the
    /// deno compile cache is hot before the retry so the plugin's probe
    /// finds it that way even when the cache went cold after boot. A failed
    /// warm-up is logged and swallowed - the retry proceeds and yt-dlp
    /// decides with whatever the provider can supply.</summary>
    protected override async Task OnBeforeSessionRetryAsync(CancellationToken cancellationToken)
    {
        if (_pot is null)
        {
            return;
        }

        try
        {
            await _pot.WarmUpDenoAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Deno warm-up before the session retry failed; proceeding without it");
        }
    }

    /// <summary>
    /// Different extractor keys (youtube, youtubepot-bgutilhttp) require
    /// SEPARATE --extractor-args values: joining them with a semicolon into
    /// one value would make them unknown youtube sub-args.
    /// </summary>
    private static void SetExtractorArgs(OptionSet options, string primary, string? extra)
    {
        if (extra is null)
        {
            options.ExtractorArgs = primary;
            return;
        }

        options.ExtractorArgs = new[] { primary, extra };
    }

    /// <summary>Hands the run a one-time COPY of the snapshot (never the
    /// original - yt-dlp rewrites the cookies file it is given).</summary>
    protected override Task<string?> AcquireSessionCookieFileAsync(string taskId, CancellationToken cancellationToken)
    {
        if (!_snapshot.Exists)
        {
            return Task.FromResult<string?>(null);
        }

        var copy = _snapshot.CreateSnapshotCopy();
        if (copy is not null)
        {
            Log.Information("Task {TaskId}: session snapshot copy attached ({File})",
                taskId, Path.GetFileName(copy));
        }

        return Task.FromResult<string?>(copy);
    }

    /// <summary>A rotated-session report (even at exit code 0) schedules a
    /// throttled re-export of the session.</summary>
    protected override void OnSessionRotated(IReadOnlyList<string> output, string taskId)
    {
        _export?.TriggerReExport();
    }

    /// <summary>Exact host/suffix matching: substring tests also claimed
    /// notyoutube.com or any URL with youtube.com in its query string.</summary>
    private static readonly DomainSuffixMatcher YouTubeDomains = new(new[] { "youtube.com", "youtu.be" });

    public override bool CanHandle(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && YouTubeDomains.IsMatch(uri.Host);

    /// <summary>
    /// True when the landing URL indicates a login or verification page
    /// (accounts.google.com, Google servicelogin, /sorry challenge, generic
    /// signin/login paths), meaning the presented session was not accepted.
    /// Also used by the session export pipeline's web health gate.
    /// </summary>
    public static bool IsLoginRedirectUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        return url.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase)
            || url.Contains("servicelogin", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/sorry/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("signin", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/login", StringComparison.OrdinalIgnoreCase);
    }
}
