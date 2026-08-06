namespace FlexFetch.Services.Downloaders;

/// <summary>
/// Registers downloader plugins and selects candidates for a URL:
/// matching rules first, then attempt priority, with a guaranteed fallback.
/// </summary>
public sealed class DownloaderFactory
{
    private readonly IReadOnlyList<IDownloader> _downloaders;

    public DownloaderFactory(IEnumerable<IDownloader> downloaders)
    {
        _downloaders = downloaders.OrderByDescending(d => d.Priority).ToList();
    }

    public IReadOnlyList<IDownloader> All => _downloaders;

    /// <summary>Downloaders that claim the URL, ordered by priority (highest first).</summary>
    public IReadOnlyList<IDownloader> SelectDownloaders(string url) =>
        _downloaders.Where(d => d.CanHandle(url)).ToList();

    /// <summary>The guaranteed fallback downloader (lowest priority).</summary>
    public IDownloader? Fallback => _downloaders.LastOrDefault();
}
