using FlexFetch.Entities;
using FlexFetch.Services.Routing;
using FlexFetch.Services.Tasks;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Services.Downloaders;

/// <summary>
/// Bridges TaskService and the downloader plugins: selects a downloader by
/// match rules, analyzes the URL, expands playlists into child tasks and
/// downloads - degrading to the next candidate on failure. Direct media
/// links (by extension, or by HEAD-probed Content-Type when the URL has no
/// extension) are handed to the generic file downloader right away instead
/// of wasting a slow yt-dlp attempt.
/// </summary>
public sealed class DownloaderTaskExecutor : ITaskExecutor
{
    private readonly DownloaderFactory _factory;
    private readonly IProxyService _proxy;
    private readonly ILogger _log;

    /// <summary>Creates a child task (parent, child, referrer) -> child id.</summary>
    private readonly Func<TaskItem, MediaChild, string?, string> _submitChild;

    public DownloaderTaskExecutor(
        DownloaderFactory factory,
        IProxyService proxy,
        ILogger log,
        Func<TaskItem, MediaChild, string?, string>? submitChild = null)
    {
        _factory = factory;
        _proxy = proxy;
        _log = log;
        _submitChild = submitChild
            ?? ((_, _, _) => throw new InvalidOperationException("Child task submission is not configured"));
    }

    public async Task<TaskExecutionResult> ExecuteAsync(
        TaskItem task,
        Action<double> progress,
        CancellationToken cancellationToken)
    {
        var candidates = _factory.SelectDownloaders(task.Url);
        candidates = await PromoteDirectLinkAsync(candidates, task.Url, cancellationToken);
        Exception? lastError = null;

        foreach (var downloader in candidates)
        {
            try
            {
                var analysis = await downloader.AnalyzeAsync(task.Url, task.Id, cancellationToken);
                task.DownloaderType = downloader.Type;
                task.ContentText = string.IsNullOrWhiteSpace(analysis.ContentText) ? task.ContentText : analysis.ContentText;
                _log.Information("Task {TaskId}: using downloader {Type}", task.Id, downloader.Type);

                if (analysis.Children.Count > 0)
                {
                    foreach (var child in analysis.Children)
                    {
                        _submitChild(task, child, analysis.Referrer);
                    }

                    return TaskExecutionResult.Expanded;
                }

                await downloader.DownloadAsync(task, analysis, progress, cancellationToken);
                return TaskExecutionResult.Completed;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _log.Warning(ex, "Task {TaskId}: downloader {Type} failed, trying next", task.Id, downloader.Type);
            }
        }

        throw lastError
            ?? new InvalidOperationException($"No downloader can handle URL: {task.Url}");
    }

    /// <summary>
    /// When the URL has no media extension but the server serves a media
    /// Content-Type (direct video without a suffix), promote the generic
    /// downloader to the front so the file is downloaded directly.
    /// </summary>
    private async Task<IReadOnlyList<IDownloader>> PromoteDirectLinkAsync(
        IReadOnlyList<IDownloader> candidates,
        string url,
        CancellationToken cancellationToken)
    {
        if (DirectLinkDetector.HasMediaExtension(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return candidates;
        }

        // Only probe extension-less URLs; URLs with a page-like extension
        // (e.g. .html) are pages and go through the normal chain.
        if (!string.IsNullOrEmpty(Path.GetExtension(uri.AbsolutePath)))
        {
            return candidates;
        }

        var contentType = await DirectLinkDetector.ProbeContentTypeAsync(url, _proxy, cancellationToken);
        if (!DirectLinkDetector.IsMediaContentType(contentType))
        {
            return candidates;
        }

        var generic = candidates.FirstOrDefault(d => d.Type == "Generic");
        if (generic is null || candidates[0] == generic)
        {
            return candidates;
        }

        _log.Information("Direct media link detected by Content-Type {ContentType}", contentType);
        return new[] { generic }.Concat(candidates.Where(d => d != generic)).ToList();
    }
}
