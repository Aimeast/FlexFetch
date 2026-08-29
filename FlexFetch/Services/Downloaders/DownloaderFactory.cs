using FlexFetch.Services.Refresh;
using Microsoft.Extensions.DependencyInjection;

namespace FlexFetch.Services.Downloaders;

/// <summary>
/// Discovers and registers downloader plugins, then selects candidates for a
/// URL: a domain-specific downloader (e.g. YouTube/Twitter) that claims the
/// URL is used alone - it fails fast with no fallback; only when no specific
/// downloader matches is the generic chain (yt-dlp -> Html -> Browser ->
/// Generic) used, in that fixed order. Plugins are self-discovered via
/// reflection over the assembly - adding a new IDownloader requires no manual
/// registration.
/// </summary>
public sealed class DownloaderFactory
{
    /// <summary>Fixed attempt order of the generic fallback chain.</summary>
    private static readonly string[] GenericChainOrder = { "Ytdlp", "Html", "Browser", "Generic" };

    private readonly IReadOnlyList<IDownloader> _downloaders;

    public DownloaderFactory(IEnumerable<IDownloader> downloaders)
    {
        _downloaders = downloaders.ToList();
    }

    /// <summary>
    /// Scans the assembly for concrete IDownloader implementations and
    /// instantiates each with constructor dependencies resolved from the
    /// service provider. Optional constructor parameters fall back to their
    /// default values (e.g. injectable fetch delegates in tests).
    /// </summary>
    public static DownloaderFactory Create(IServiceProvider services)
    {
        var types = typeof(IDownloader).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(IDownloader).IsAssignableFrom(t));

        var downloaders = types
            .Select(t => (IDownloader)ActivatorUtilities.CreateInstance(services, t))
            .ToList();

        return new DownloaderFactory(downloaders);
    }

    public IReadOnlyList<IDownloader> All => _downloaders;

    /// <summary>
    /// Refresh strategies carried by downloader plugins (site-specific cookie
    /// refresh behavior, e.g. YouTube's session-rejection checks).
    /// </summary>
    public IReadOnlyList<ICookieRefreshStrategy> RefreshStrategies =>
        _downloaders.OfType<ICookieRefreshStrategy>().ToList();

    /// <summary>
    /// Cross-domain cookie mappings carried by downloader plugins (e.g.
    /// YouTube sharing Google identity cookies to sibling domains).
    /// </summary>
    public IReadOnlyList<ICookieDomainMapping> DomainMappings =>
        _downloaders.OfType<ICookieDomainMapping>().ToList();

    /// <summary>
    /// Selects downloader candidates for a URL. A matching domain-specific
    /// downloader is returned alone (more than one is a runtime error); with
    /// no specific match the generic chain is returned in its fixed order,
    /// promoting the generic file downloader to the front for direct media
    /// links so a direct file downloads straight away.
    /// </summary>
    public IReadOnlyList<IDownloader> SelectDownloaders(string url)
    {
        var specific = _downloaders
            .Where(d => d.IsDomainSpecific && d.CanHandle(url))
            .ToList();
        if (specific.Count > 1)
        {
            throw new InvalidOperationException(
                $"Multiple domain-specific downloaders match {url}: {string.Join(", ", specific.Select(d => d.Type))}");
        }

        if (specific.Count == 1)
        {
            return specific;
        }

        var candidates = _downloaders
            .Where(d => !d.IsDomainSpecific && d.CanHandle(url))
            .OrderBy(d => Array.IndexOf(GenericChainOrder, d.Type))
            .ToList();
        // Direct file links skip the slow yt-dlp attempt and download straight
        // away - except playlist manifests (.m3u/.m3u8/.mpd), which yt-dlp must
        // parse first: an HLS manifest becomes a playable stream, a plain m3u
        // list expands into playlist entries (child tasks).
        if (DirectLinkDetector.HasMediaExtension(url) && !DirectLinkDetector.IsManifestExtension(url))
        {
            var generic = candidates.FirstOrDefault(d => d.Type == "Generic");
            if (generic is not null && candidates.Count > 0 && candidates[0] != generic)
            {
                candidates.Remove(generic);
                candidates.Insert(0, generic);
            }
        }

        return candidates;
    }

    /// <summary>The guaranteed fallback downloader (end of the generic chain).</summary>
    public IDownloader? Fallback => _downloaders.FirstOrDefault(d => d.Type == "Generic");
}
