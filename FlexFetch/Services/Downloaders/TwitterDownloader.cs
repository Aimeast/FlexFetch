using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlexFetch.Entities;
using FlexFetch.Services.Routing;
using Serilog;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Services.Downloaders;

/// <summary>
/// Twitter/X downloader: resolves the post's own media through the syndication
/// embed endpoint (tweet-result JSON with mediaDetails: photo URLs and
/// video_info variants), falling back to scraping the status page HTML
/// (og:image media ids plus the mp4 variant JSON embedded in the page) when
/// the endpoint refuses. Each media item becomes a child task downloaded with
/// the status page as Referrer; video variants are reduced to the
/// highest-bitrate mp4, photos are requested at original size.
/// </summary>
public sealed partial class TwitterDownloader : IDownloader
{
    private const int FetchTimeoutSeconds = 60;

    private readonly IProxyService _proxy;
    private readonly ILogger _log;
    private readonly Func<string, CancellationToken, Task<string>> _fetchText;

    public TwitterDownloader(IProxyService proxy, ILogger log, Func<string, CancellationToken, Task<string>>? fetchText = null)
    {
        _proxy = proxy;
        _log = log;
        _fetchText = fetchText ?? FetchTextAsync;
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
            if (TryParsePayload(json, out var post))
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
                _log.Information("Twitter analysis of {Url} via syndication: {Count} media items", url, analysis.Children.Count);
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
            throw new InvalidOperationException($"No downloadable media found on {url}");
        }

        return fallback;
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
