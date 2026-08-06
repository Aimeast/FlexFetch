using Microsoft.Extensions.DependencyInjection;

namespace FlexFetch.Services.Downloaders;

/// <summary>
/// Discovers and registers downloader plugins, then selects candidates for a
/// URL: matching rules first, then attempt priority, with a guaranteed
/// fallback. Plugins are self-discovered via reflection over the assembly —
/// adding a new IDownloader requires no manual registration.
/// </summary>
public sealed class DownloaderFactory
{
    private readonly IReadOnlyList<IDownloader> _downloaders;

    public DownloaderFactory(IEnumerable<IDownloader> downloaders)
    {
        _downloaders = downloaders.OrderByDescending(d => d.Priority).ToList();
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
    /// Downloaders that claim the URL, ordered by priority (highest first).
    /// For direct media links (extension-based) the generic file downloader
    /// is promoted to the front: a direct file should be downloaded straight
    /// away instead of wasting a slow yt-dlp attempt.
    /// </summary>
    public IReadOnlyList<IDownloader> SelectDownloaders(string url)
    {
        var candidates = _downloaders.Where(d => d.CanHandle(url)).ToList();
        if (DirectLinkDetector.HasMediaExtension(url))
        {
            var generic = candidates.FirstOrDefault(d => d.Type == "Generic");
            if (generic is not null && candidates[0] != generic)
            {
                candidates.Remove(generic);
                candidates.Insert(0, generic);
            }
        }

        return candidates;
    }

    /// <summary>The guaranteed fallback downloader (lowest priority).</summary>
    public IDownloader? Fallback => _downloaders.LastOrDefault();
}
