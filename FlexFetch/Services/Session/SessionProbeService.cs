using FlexFetch.Config;
using FlexFetch.Services.Downloaders;
using FlexFetch.Services.Routing;
using Newtonsoft.Json;
using Serilog;
using YoutubeDLSharp;
using YoutubeDLSharp.Metadata;
using YoutubeDLSharp.Options;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Services.Session;

/// <summary>Result of one InnerTube probe run.</summary>
public sealed record ProbeResult(
    bool RunSucceeded,
    YtdlpOutputClass Class,
    IReadOnlyList<string> Output,
    string VideoTitle)
{
    /// <summary>True only when the run succeeded AND no fatal marker appeared:
    /// exit code 0 with a rotation WARNING still counts as unhealthy (yt-dlp
    /// falls back to anonymous "success").</summary>
    public bool Healthy => RunSucceeded && Class == YtdlpOutputClass.Ok;
}

/// <summary>
/// InnerTube probe - the authoritative session health gate. Health is judged
/// on the surface yt-dlp actually consumes, never on the browser/web surface
/// (a device-bound session can be rejected by web while InnerTube accepts).
/// The probe always runs with a one-time cookie COPY, never the snapshot.
/// Every probe is bounded by a hard timeout: a hung process (e.g. a blocked
/// direct connection) must never hold the caller - the export lock or an
/// HTTP request - forever.
/// </summary>
public sealed class SessionProbeService
{
    /// <summary>Hard per-probe timeout; a metadata fetch takes seconds when
    /// the network path works at all.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    private readonly YtdlpService _ytdlp;
    private readonly IProxyService _proxy;
    private readonly PotProviderService? _pot;
    private readonly ILogger _log;

    public SessionProbeService(YtdlpService ytdlp, IProxyService proxy, ILogger log,
        PotProviderService? pot = null)
    {
        _ytdlp = ytdlp;
        _proxy = proxy;
        _pot = pot;
        _log = log;
    }

    /// <summary>
    /// Runs one metadata probe against the probe URL with the given cookie
    /// file (a copy or candidate jar - never the snapshot itself) and client
    /// posture. Returns the classified result; a timeout comes back as a
    /// Retryable verdict instead of hanging. When <paramref name="progress"/>
    /// is given, yt-dlp's own stderr lines ("Downloading webpage", the PO
    /// token generation, ...) are forwarded as they arrive so callers can
    /// show what the probe is doing right now.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(
        string probeUrl,
        string? cookieFilePath,
        string extractorArgs,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null,
        IProgress<string>? progress = null)
    {
        var effective = timeout ?? DefaultTimeout;
        await _ytdlp.EnsureInstalledAsync(cancellationToken);

        // Mirrors what YoutubeDL.RunVideoDataFetch used to build (dump-json +
        // flat playlist, no config, no playlist walk), without its buffered
        // output: the process events stream lines while the run is alive.
        var options = new OptionSet
        {
            DumpSingleJson = true,
            FlatPlaylist = true,
            IgnoreConfig = true,
            NoPlaylist = true,
            ExtractorArgs = extractorArgs,
        };
        // Mirror the download posture: the probe gets the same script-deno
        // token source as real downloads.
        if (_pot is not null)
        {
            options.ExtractorArgs = new[] { extractorArgs, _pot.ScriptPathArg };
        }
        if (cookieFilePath is not null)
        {
            options.Cookies = cookieFilePath;
        }

        var proxyUri = _proxy.GetProxyUri(new Uri(probeUrl));
        if (proxyUri is not null)
        {
            options.Proxy = YtdlpDownloader.NormalizeProxyForYtdlp(proxyUri);
        }

        _log.Information("Session probe: {Url} cookies={Cookies} args={Args} timeout={Timeout}s",
            probeUrl, cookieFilePath is not null ? "attached" : "anonymous", extractorArgs, effective.TotalSeconds);

        // Linked CTS: the hard timeout cancels the yt-dlp process (kills the
        // tree), while a caller shutdown still propagates.
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(effective);

        // stderr carries yt-dlp's log lines (warnings, extractor and PO token
        // progress) - collected for the classifier and streamed live; stdout
        // carries the single-line -J JSON dump, kept when it parses.
        var process = new YoutubeDLProcess(_ytdlp.BinaryPath);
        var stderr = new List<string>();
        var stderrLock = new object();
        VideoData? data = null;
        process.ErrorReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (stderrLock)
            {
                stderr.Add(e.Data);
            }
            progress?.Report(e.Data);
        };
        process.OutputReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data))
            {
                return;
            }

            try
            {
                data = JsonConvert.DeserializeObject<VideoData>(e.Data);
            }
            catch (JsonException)
            {
                // Not the JSON dump: the library routed such lines into the
                // error output - keep them for the classifier as well.
                lock (stderrLock)
                {
                    stderr.Add(e.Data);
                }
            }
        };

        int exitCode;
        try
        {
            exitCode = await process.RunAsync(new[] { probeUrl }, options, bounded.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own timeout fired (the caller's token is still live): a hung
            // network path is a retryable condition, never an endless wait.
            _log.Warning("Session probe timed out after {Seconds}s: {Url}", effective.TotalSeconds, probeUrl);
            return new ProbeResult(
                false,
                YtdlpOutputClass.Retryable,
                new[] { $"ERROR: probe timed out after {effective.TotalSeconds:F0}s (network path to the probe URL is broken)" },
                string.Empty);
        }

        List<string> lines;
        lock (stderrLock)
        {
            lines = new List<string>(stderr);
        }

        var success = exitCode == 0;
        var class_ = YtdlpOutputClassifier.Classify(lines, success);
        var title = data?.Title ?? string.Empty;

        _log.Information("Session probe verdict: {Class} (run success={Success}, {Lines} output lines)",
            class_, success, lines.Count);
        return new ProbeResult(success, class_, lines, title);
    }

    /// <summary>Probe URL from configuration (a stable public video).</summary>
    public string GetProbeUrl(IConfiguration config) =>
        ConfigRegistry.From(config, ConfigKeys.SessionProbeUrl);
}
