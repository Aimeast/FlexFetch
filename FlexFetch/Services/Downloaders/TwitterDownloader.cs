using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlexFetch.Entities;
using FlexFetch.Services.Routing;
using FlexFetch.Services.Session;
using Serilog;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Services.Downloaders;

/// <summary>
/// Twitter/X downloader: resolves the post's own media through the syndication
/// embed endpoint (tweet-result JSON with mediaDetails: photo URLs and
/// video_info variants). When that endpoint refuses (tombstone: sensitive or
/// restricted posts are gated for anonymous viewers) and an X session is
/// imported, the GraphQL TweetResultByRestId endpoint is called with the
/// session's auth_token; the last resort is scraping the status page HTML
/// (og:image media ids plus the mp4 variant JSON embedded in the page). Each
/// media item becomes a child task downloaded with the status page as
/// Referrer; video variants are reduced to the highest-bitrate mp4, photos
/// are requested at original size.
/// </summary>
public sealed partial class TwitterDownloader : IDownloader
{
    private const int FetchTimeoutSeconds = 60;

    /// <summary>The public web client bearer token (anonymous, ships with
    /// every x.com browser session) that accompanies GraphQL calls.</summary>
    private const string WebBearerToken = "AAAAAAAAAAAAAAAAAAAAANRILgAAAAAAnNwIzUejRCOuH5E6I8xnZz4puTs"
        + "%3D1Zv7ttfk8LF81IUq16cHjhLTvJu4FA33AGWWjCpTnA";

    /// <summary>How long the scraped GraphQL descriptor (query id + feature
    /// switches) may be reused before the main bundle is scraped again.</summary>
    private static readonly TimeSpan DescriptorLifetime = TimeSpan.FromHours(6);

    private static readonly object DescriptorLock = new();
    private static (string QueryId, string Features, DateTime FetchedAt)? DescriptorCache;

    private readonly IProxyService _proxy;
    private readonly ILogger _log;
    private readonly Func<string, CancellationToken, Task<string>> _fetchText;

    /// <summary>Injectable HTTP runner for the authenticated GraphQL flow
    /// (tests); must keep UseCookies=false so Set-Cookie stays visible.</summary>
    private readonly Func<string, IReadOnlyDictionary<string, string>?, CancellationToken, Task<HttpResponseMessage>> _send;

    /// <summary>Null in tests that do not care about the session: the
    /// GraphQL fallback is skipped and anonymous errors stay anonymous.</summary>
    private readonly TwitterSessionStore? _twitterSession;

    public TwitterDownloader(
        IProxyService proxy,
        ILogger log,
        Func<string, CancellationToken, Task<string>>? fetchText = null,
        TwitterSessionStore? twitterSession = null,
        Func<string, IReadOnlyDictionary<string, string>?, CancellationToken, Task<HttpResponseMessage>>? sendAsync = null)
    {
        _proxy = proxy;
        _log = log;
        _fetchText = fetchText ?? FetchTextAsync;
        _twitterSession = twitterSession;
        _send = sendAsync ?? DefaultSendAsync;
    }

    public string Type => "Twitter";

    public bool IsDomainSpecific => true;

    public bool CanHandle(string url) =>
        url.Contains("x.com", StringComparison.OrdinalIgnoreCase)
        || url.Contains("twitter.com", StringComparison.OrdinalIgnoreCase);

    public async Task<AnalysisResult> AnalyzeAsync(string url, string taskId, CancellationToken cancellationToken)
    {
        var statusId = StatusIdRegex().Match(url).Groups[1].Value;

        if (statusId.Length > 0)
        {
            var json = await TryFetchSyndicationAsync(url, statusId, cancellationToken);
            if (!TryParsePayload(json, out var post))
            {
                // Tombstoned or endpoint down: sensitive/restricted posts are
                // anonymous-gated exactly this way - retry with the session
                // over GraphQL before giving the page scrape a chance.
                post = await TryAnalyzeViaGraphQLAsync(url, statusId, cancellationToken);
            }

            if (post is not null)
            {
                // A repost carries no text of its own (the body is just the
                // t.co media link): borrow the title from the post the media
                // originally came from, one hop along expanded_url.
                var text = post.Text;
                if (OneLine(text).Length == 0)
                {
                    text = await TryResolveSourceTextAsync(post, statusId, cancellationToken) ?? text;
                }

                var analysis = BuildAnalysis(post, text, url);
                _lastAnalysisSource = "syndication";
                _log.Information("Twitter analysis of {Url} via {Source}: {Count} media items",
                    url, _lastAnalysisSource, analysis.Children.Count);
                if (analysis.Children.Count == 0)
                {
                    throw new InvalidOperationException($"Post {statusId} contains no downloadable media (text-only post)");
                }

                return analysis;
            }
        }

        // Fallback: scrape the status page (also the path for non-status URLs).
        var page = await _fetchText(url, cancellationToken);
        var fallback = BuildFromPage(page, url);
        _log.Information("Twitter analysis of {Url} via page: {Count} media items", url, fallback.Children.Count);
        if (fallback.Children.Count == 0)
        {
            if (statusId.Length > 0 && !HasSession())
            {
                throw new AuthRequiredException(AuthFailureReason.LoginRequired,
                    $"X refuses anonymous access to post {statusId} (sensitive, age-restricted, or removed content). "
                    + "Import an X session on the Session page and retry.");
            }

            throw new InvalidOperationException($"No downloadable media found on {url}");
        }

        return fallback;
    }

    /// <summary>True when an X session with an auth_token is available.</summary>
    private bool HasSession()
    {
        if (_twitterSession is null || !_twitterSession.Exists)
        {
            return false;
        }

        return _twitterSession.ReadCookies()
            .Any(c => c.Name.Equals("auth_token", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The authenticated path for gated posts: derive a fresh csrf token
    /// (x.com sets ct0 for a presented auth_token), scrape the GraphQL
    /// descriptor (query id + feature switches) from the web client's main
    /// bundle, then call TweetResultByRestId. Returns null on any failure -
    /// the caller falls through to the page scrape.
    /// </summary>
    private async Task<TweetPayload?> TryAnalyzeViaGraphQLAsync(string url, string statusId, CancellationToken cancellationToken)
    {
        if (_twitterSession is null || !HasSession())
        {
            return null;
        }

        var authToken = _twitterSession.ReadCookies()
            .First(c => c.Name.Equals("auth_token", StringComparison.OrdinalIgnoreCase)).Value;

        var ct0 = await DeriveCsrfAsync(authToken, cancellationToken);
        if (ct0 is null)
        {
            // The token no longer carries a session; the stored verdict is
            // already marked Invalid. Anonymous fallbacks may still work.
            _log.Warning("Twitter session is no longer accepted by x.com; re-import it on the Session page");
            return null;
        }

        var (queryId, features) = await GetDescriptorAsync(force: false, authToken, cancellationToken);
        var json = await FetchTweetResultAsync(queryId, features, statusId, authToken, ct0, cancellationToken);
        if (json is null)
        {
            // A stale descriptor (rotated bundle) is the common 400 cause:
            // scrape it once more and retry before giving up.
            (queryId, features) = await GetDescriptorAsync(force: true, authToken, cancellationToken);
            json = await FetchTweetResultAsync(queryId, features, statusId, authToken, ct0, cancellationToken);
            if (json is null)
            {
                return null;
            }
        }

        if (TryParseGraphQLPayload(json, out var payload, out var parseFailReason))
        {
            _lastAnalysisSource = "graphql";
            return payload;
        }

        // The excerpt distinguishes an expected tombstone from an errors[]
        // body or a shape the parser does not know yet.
        _log.Information("Twitter GraphQL payload for {StatusId} is not a readable tweet ({Reason}): {Excerpt}",
            statusId, parseFailReason, json.Length > 600 ? json[..600] : json);
        return null;
    }

    private string? _lastAnalysisSource;

    /// <summary>GETs x.com with the auth_token and returns the freshly set
    /// ct0 csrf cookie, or null when x.com refuses the token (revoked).</summary>
    private async Task<string?> DeriveCsrfAsync(string authToken, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await _send("https://x.com/",
                new Dictionary<string, string> { ["Cookie"] = $"auth_token={authToken}" }, timeout.Token);
            var setCookies = response.Headers.TryGetValues("Set-Cookie", out var values)
                ? values.ToList()
                : new List<string>();
            var ct0 = TwitterSessionStore.ExtractSetCookieValue(setCookies, "ct0");
            if (ct0 is null)
            {
                _twitterSession.UpdateMeta(m =>
                {
                    m.LastCheckAt = DateTime.UtcNow;
                    m.Health = TwitterSessionHealth.Invalid.ToString();
                    m.LastError = "x.com no longer accepts the auth_token (no fresh ct0); export fresh cookies";
                });
            }

            return ct0;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            _log.Warning("Deriving the X csrf token failed: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>Returns the cached GraphQL descriptor or scrapes it from the
    /// web client bundle: the query id rotates with every frontend deploy.
    /// The home page must be fetched WITH the session cookie - the anonymous
    /// landing page is a marketing shell without any bundle reference.</summary>
    private async Task<(string QueryId, string Features)> GetDescriptorAsync(
        bool force, string authToken, CancellationToken cancellationToken)
    {
        lock (DescriptorLock)
        {
            if (!force && DescriptorCache is { } cached && DateTime.UtcNow - cached.FetchedAt < DescriptorLifetime)
            {
                return (cached.QueryId, cached.Features);
            }
        }

        var home = await _send("https://x.com/",
            new Dictionary<string, string> { ["Cookie"] = $"auth_token={authToken}" }, cancellationToken);
        string html;
        using (home)
        {
            html = await home.Content.ReadAsStringAsync(cancellationToken);
        }

        var bundleUrl = MainBundleRegex().Match(html).Groups[1].Value;
        if (bundleUrl.Length == 0)
        {
            throw new InvalidOperationException("x.com page does not reference the main bundle");
        }

        var bundle = await _send(bundleUrl, null, cancellationToken);
        string js;
        using (bundle)
        {
            js = await bundle.Content.ReadAsStringAsync(cancellationToken);
        }

        var match = OperationRegex().Match(js);
        if (!match.Success)
        {
            throw new InvalidOperationException("TweetResultByRestId operation not found in the main bundle");
        }

        var names = FeatureSwitchRegex().Matches(match.Groups[2].Value)
            .Select(m => $"\"{m.Groups[1].Value}\":true");
        var descriptor = (match.Groups[1].Value, "{" + string.Join(",", names) + "}");
        lock (DescriptorLock)
        {
            DescriptorCache = (descriptor.Item1, descriptor.Item2, DateTime.UtcNow);
        }

        return descriptor;
    }

    private async Task<string?> FetchTweetResultAsync(
        string queryId, string features, string statusId, string authToken, string ct0, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var variables = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["tweetId"] = statusId,
                ["withCommunity"] = false,
                ["includePromotedContent"] = false,
                ["withVoice"] = false,
            });
            var url = "https://x.com/i/api/graphql/" + queryId + "/TweetResultByRestId"
                + "?variables=" + Uri.EscapeDataString(variables)
                + "&features=" + Uri.EscapeDataString(features);
            var headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer " + WebBearerToken,
                ["x-csrf-token"] = ct0,
                ["Cookie"] = $"auth_token={authToken}; ct0={ct0}",
            };
            using var response = await _send(url, headers, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                // A 400 with a stale descriptor is the common rotation
                // failure; null sends the caller into one refresh + retry.
                _log.Information("Twitter GraphQL answered {Status}", (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            _log.Warning("Twitter GraphQL call failed: {Message}", ex.Message);
            return null;
        }
    }

    private async Task<HttpResponseMessage> DefaultSendAsync(
        string url, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        var uri = new Uri(url);
        var handler = _proxy.CreateHandler(uri);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(FetchTimeoutSeconds) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        client.DefaultRequestHeaders.Referrer = new Uri("https://x.com/");
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                client.DefaultRequestHeaders.Add(name, value);
            }
        }

        return await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    /// <summary>
    /// Parses the TweetResultByRestId JSON. The result may be wrapped in a
    /// TweetWithVisibilityResults envelope (limited-action gating); a
    /// tombstone or anything without legacy media parses as "not a tweet".
    /// </summary>
    private static bool TryParseGraphQLPayload(string? json, out TweetPayload payload, out string reason)
    {
        payload = new TweetPayload(string.Empty, null, Array.Empty<TweetMedia>());
        reason = "empty";
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("tweetResult", out var tweetResult)
                || !tweetResult.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Object)
            {
                reason = "no data.tweetResult.result";
                return false;
            }

            var typename = result.TryGetProperty("__typename", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : "(none)";
            if (result.TryGetProperty("tweet", out var unwrapped)
                && unwrapped.ValueKind == JsonValueKind.Object)
            {
                result = unwrapped;
            }

            if (!result.TryGetProperty("legacy", out var legacy)
                || legacy.ValueKind != JsonValueKind.Object)
            {
                reason = $"no legacy (typename={typename})";
                return false;
            }

            string? author = null;
            if (result.TryGetProperty("core", out var core)
                && core.TryGetProperty("user_results", out var userResults)
                && userResults.TryGetProperty("result", out var userResult))
            {
                // The current shape carries the identity under result.core
                // (name/screen_name); older shapes used result.legacy.
                foreach (var identity in new[] { "core", "legacy" })
                {
                    if (!userResult.TryGetProperty(identity, out var identityBlock)
                        || identityBlock.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (identityBlock.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                    {
                        author = name.GetString();
                        break;
                    }

                    if (identityBlock.TryGetProperty("screen_name", out var screenName) && screenName.ValueKind == JsonValueKind.String)
                    {
                        author = screenName.GetString();
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(author))
            {
                reason = $"no author (typename={typename})";
                return false;
            }

            string? text = null;
            if (legacy.TryGetProperty("full_text", out var fullText) && fullText.ValueKind == JsonValueKind.String)
            {
                text = fullText.GetString();
            }

            var media = new List<TweetMedia>();
            if (legacy.TryGetProperty("extended_entities", out var entities)
                && entities.TryGetProperty("media", out var items)
                && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in items.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    string? mediaUrl = entry.TryGetProperty("media_url_https", out var mediaUrlElement)
                        && mediaUrlElement.ValueKind == JsonValueKind.String
                            ? mediaUrlElement.GetString()
                            : null;
                    string? expandedUrl = entry.TryGetProperty("expanded_url", out var expandedUrlElement)
                        && expandedUrlElement.ValueKind == JsonValueKind.String
                            ? expandedUrlElement.GetString()
                            : null;
                    media.Add(new TweetMedia(mediaUrl, expandedUrl, ParseVariants(entry)));
                }
            }

            payload = new TweetPayload(author!, text, media);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Twitter analysis always expands children; nothing to download here.</summary>
    public Task DownloadAsync(TaskItem task, AnalysisResult analysis, Action<double> progress, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Twitter tasks are expanded into child tasks, nothing to download directly");

    private async Task<string?> TryFetchSyndicationAsync(string url, string statusId, CancellationToken cancellationToken)
    {
        string json;
        try
        {
            json = await _fetchText($"https://cdn.syndication.twimg.com/tweet-result?id={statusId}&token=a", cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _log.Warning("Twitter syndication unavailable for {Url}: {Message}", url, ex.Message);
            return null;
        }

        return json;
    }

    private static AnalysisResult BuildAnalysis(TweetPayload post, string? text, string referrer)
    {
        var title = BuildTitle(text, post.Author);
        var children = new List<MediaChild>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var media in post.Media)
        {
            var mediaUrl = ResolveMediaUrl(media);
            if (mediaUrl is null || !seen.Add(mediaUrl))
            {
                continue;
            }

            // No child title: the child keeps the CDN's original file name
            // (e.g. lOsfnrGVhWLzpyQG.mp4), which stays unique when a post
            // carries several videos.
            children.Add(new MediaChild { Url = mediaUrl, DownloaderType = "Generic" });
        }

        return new AnalysisResult
        {
            Title = title,
            // A body that reduces to nothing (bare t.co links) shows as no text.
            ContentText = OneLine(text).Length == 0 ? null : text,
            Referrer = referrer,
            Children = children,
        };
    }

    /// <summary>
    /// Follows the media's expanded_url to the post the media came from (a
    /// repost's own body is only the t.co link) and returns that post's text.
    /// One hop only; any failure yields null and the caller keeps its fallback.
    /// </summary>
    private async Task<string?> TryResolveSourceTextAsync(TweetPayload post, string statusId, CancellationToken cancellationToken)
    {
        var sourceId = post.Media
            .Select(m => m.ExpandedUrl)
            .Select(u => u is null ? string.Empty : StatusIdRegex().Match(u).Groups[1].Value)
            .FirstOrDefault(id => id.Length > 0 && id != statusId) ?? string.Empty;
        if (sourceId.Length == 0)
        {
            return null;
        }

        string json;
        try
        {
            json = await _fetchText($"https://cdn.syndication.twimg.com/tweet-result?id={sourceId}&token=a", cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _log.Warning("Twitter source post unavailable for {StatusId}: {Message}", sourceId, ex.Message);
            return null;
        }

        return TryParsePayload(json, out var source) ? source.Text : null;
    }

    private static AnalysisResult BuildFromPage(string page, string url)
    {
        var description = WebUtility.HtmlDecode(DescriptionRegex().Match(page).Groups[1].Value);
        var title = BuildTitle(description, null);

        var children = new List<MediaChild>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // og:image references the post's own media: video thumbs carry a numeric
        // media id (used to scope the variant JSON below), photo URLs are
        // downloadable media themselves. Anything else on the page (quoted
        // posts, replies) must not leak into the children.
        var videoIds = new HashSet<string>();
        foreach (Match og in OgImageRegex().Matches(page))
        {
            var ogUrl = WebUtility.HtmlDecode(og.Groups[1].Value);
            var mediaId = ThumbIdRegex().Match(ogUrl).Groups[1].Value;
            if (mediaId.Length > 0)
            {
                videoIds.Add(mediaId);
            }
            else if (ogUrl.Contains("/media/", StringComparison.OrdinalIgnoreCase) && seen.Add(ogUrl))
            {
                children.Add(new MediaChild { Url = PhotoUrlForDownload(ogUrl), DownloaderType = "Generic" });
            }
        }

        if (videoIds.Count > 0)
        {
            foreach (var mediaUrl in SelectBestVariants(page, videoIds))
            {
                if (seen.Add(mediaUrl))
                {
                    children.Add(new MediaChild { Url = mediaUrl, DownloaderType = "Generic" });
                }
            }
        }

        return new AnalysisResult
        {
            Title = title,
            ContentText = string.IsNullOrWhiteSpace(description) ? null : description,
            Referrer = url,
            Children = children,
        };
    }

    /// <summary>
    /// Extracts the mp4 variant JSON the page embeds for the post's videos and
    /// keeps the highest-bitrate variant per media id, ignoring media from
    /// quoted posts and replies whose id is not referenced by og:image.
    /// </summary>
    private static IEnumerable<string> SelectBestVariants(string page, IReadOnlySet<string> videoIds)
    {
        var variantsByMedia = new Dictionary<string, List<TweetVariant>>();
        foreach (Match variant in VariantRegex().Matches(page))
        {
            var mediaId = MediaIdRegex().Match(variant.Groups[3].Value).Groups[1].Value;
            if (mediaId.Length == 0)
            {
                continue;
            }

            if (!variantsByMedia.TryGetValue(mediaId, out var list))
            {
                list = new List<TweetVariant>();
                variantsByMedia[mediaId] = list;
            }

            list.Add(new TweetVariant(ParseBitrate(variant.Groups[1].Value), variant.Groups[2].Value, variant.Groups[3].Value));
        }

        foreach (var (mediaId, variants) in variantsByMedia)
        {
            if (!videoIds.Contains(mediaId))
            {
                continue;
            }

            var best = BestVariant(variants);
            if (best?.Url is not null)
            {
                yield return best.Url;
            }
        }
    }

    private static IReadOnlyList<TweetVariant> ParseVariants(JsonElement entry)
    {
        var variants = new List<TweetVariant>();
        if (entry.TryGetProperty("video_info", out var videoInfo)
            && videoInfo.TryGetProperty("variants", out var items)
            && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("content_type", out var contentType)
                    && item.TryGetProperty("url", out var itemUrl))
                {
                    variants.Add(new TweetVariant(
                        item.TryGetProperty("bitrate", out var bitrate) && bitrate.ValueKind == JsonValueKind.Number
                            ? bitrate.GetInt32()
                            : null,
                        contentType.GetString() ?? string.Empty,
                        itemUrl.GetString() ?? string.Empty));
                }
            }
        }

        return variants;
    }

    private static TweetVariant? BestVariant(IReadOnlyList<TweetVariant> variants) =>
        variants.Where(v => v.ContentType.Equals("video/mp4", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => v.Bitrate ?? 0)
            .FirstOrDefault();

    private static string? ResolveMediaUrl(TweetMedia media)
    {
        if (media.Variants.Count > 0)
        {
            return BestVariant(media.Variants)?.Url is { Length: > 0 } videoUrl ? videoUrl : null;
        }

        return string.IsNullOrEmpty(media.MediaUrlHttps) ? null : AppendOrigSize(media.MediaUrlHttps);
    }

    /// <summary>Normalizes an og:image photo URL for download: an explicit
    /// .jpg path segment (og:image often carries none) and jpg format at
    /// original size.</summary>
    private static string PhotoUrlForDownload(string url)
    {
        var queryStart = url.IndexOf('?');
        var path = queryStart < 0 ? url : url[..queryStart];
        if (!Path.HasExtension(path))
        {
            url = path + ".jpg" + (queryStart < 0 ? string.Empty : url[queryStart..]);
        }

        return AppendOrigSize(url);
    }

    /// <summary>Rewrites the CDN size selector to the original-quality variant.</summary>
    private static string AppendOrigSize(string url)
    {
        var queryStart = url.IndexOf('?');
        if (queryStart < 0)
        {
            return url + "?name=orig";
        }

        var parameters = url[(queryStart + 1)..]
            .Split('&')
            .Select(p => p.StartsWith("format=", StringComparison.OrdinalIgnoreCase) ? "format=jpg" : p)
            .Where(p => !p.StartsWith("name=", StringComparison.OrdinalIgnoreCase))
            .ToList();
        parameters.Add("name=orig");
        return url[..(queryStart + 1)] + string.Join("&", parameters);
    }

    private static string BuildTitle(string? text, string? author)
    {
        var cleaned = OneLine(text);
        if (cleaned.Length > 0)
        {
            return cleaned;
        }

        var who = OneLine(author);
        return who.Length > 0 ? $"{who} on X" : "x-post";
    }

    /// <summary>Collapses the text to a single line and drops trailing t.co links.</summary>
    private static string OneLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = TcoSuffixRegex().Replace(value, string.Empty).Trim();
        return WhitespaceRegex().Replace(trimmed, " ");
    }

    private async Task<string> FetchTextAsync(string url, CancellationToken cancellationToken)
    {
        var uri = new Uri(url);
        using var client = new HttpClient(_proxy.CreateHandler(uri)) { Timeout = TimeSpan.FromSeconds(FetchTimeoutSeconds) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        if (CanHandle(url))
        {
            client.DefaultRequestHeaders.Referrer = new Uri("https://x.com/");
        }

        return await client.GetStringAsync(uri, cancellationToken);
    }

    private static int? ParseBitrate(string value) => int.TryParse(value, out var bitrate) ? bitrate : null;

    /// <summary>A tweet payload as reported by the syndication embed endpoint.</summary>
    private sealed record TweetPayload(string Author, string? Text, IReadOnlyList<TweetMedia> Media);

    private sealed record TweetMedia(string? MediaUrlHttps, string? ExpandedUrl, IReadOnlyList<TweetVariant> Variants);

    private sealed record TweetVariant(int? Bitrate, string ContentType, string Url);

    /// <summary>
    /// Parses the syndication tweet-result JSON. A payload counts as a real
    /// tweet only when it carries a user identity: anything else (error pages,
    /// empty stubs, rate-limit bodies) must fall through to the page scrape.
    /// </summary>
    private static bool TryParsePayload(string? json, out TweetPayload payload)
    {
        payload = new TweetPayload(string.Empty, null, Array.Empty<TweetMedia>());
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("user", out var user)
                || user.ValueKind != JsonValueKind.Object
                || !user.TryGetProperty("screen_name", out var screenName)
                || screenName.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(screenName.GetString()))
            {
                return false;
            }

            string? author = screenName.GetString();
            if (user.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                author = name.GetString();
            }

            string? text = null;
            if (root.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
            {
                text = textElement.GetString();
            }

            var media = new List<TweetMedia>();
            if (root.TryGetProperty("mediaDetails", out var details) && details.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in details.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    string? mediaUrl = null;
                    if (entry.TryGetProperty("media_url_https", out var mediaUrlElement)
                        && mediaUrlElement.ValueKind == JsonValueKind.String)
                    {
                        mediaUrl = mediaUrlElement.GetString();
                    }

                    string? expandedUrl = null;
                    if (entry.TryGetProperty("expanded_url", out var expandedUrlElement)
                        && expandedUrlElement.ValueKind == JsonValueKind.String)
                    {
                        expandedUrl = expandedUrlElement.GetString();
                    }

                    media.Add(new TweetMedia(mediaUrl, expandedUrl, ParseVariants(entry)));
                }
            }

            payload = new TweetPayload(author ?? string.Empty, text, media);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"(https://abs\.twimg\.com/responsive-web/client-web/main\.[A-Za-z0-9]+\.js)")]
    private static partial Regex MainBundleRegex();

    [GeneratedRegex(@"queryId:""([0-9A-Za-z_-]+)"",operationName:""TweetResultByRestId"",operationType:""query"",metadata:\{featureSwitches:\[([^\]]*)\]")]
    private static partial Regex OperationRegex();

    [GeneratedRegex(@"""([A-Za-z0-9_]+)""")]
    private static partial Regex FeatureSwitchRegex();

    [GeneratedRegex(@"(?:/status(?:es)?/)(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex StatusIdRegex();

    [GeneratedRegex(@"<meta[^>]*property=""og:description""[^>]*content=""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex DescriptionRegex();

    [GeneratedRegex(@"<meta[^>]*property=""og:image""[^>]*content=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex OgImageRegex();

    [GeneratedRegex(@"(?:amplify_video_thumb|ext_tw_video_thumb|video_thumb)/(\d+)/")]
    private static partial Regex ThumbIdRegex();

    [GeneratedRegex(@"\{(?:bitrate:(\d+),)?content_type:""([^""]+)"",url:""(https://video\.twimg\.com/[^""]+)""\}")]
    private static partial Regex VariantRegex();

    [GeneratedRegex(@"(\d+)/vid/")]
    private static partial Regex MediaIdRegex();

    [GeneratedRegex(@"\s*https?://t\.co/\w+$")]
    private static partial Regex TcoSuffixRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
