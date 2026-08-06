using FlexFetch.Domain;
using FlexFetch.Services.Downloaders;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Services;

/// <summary>
/// Bridges TaskService and the downloader plugins: selects a downloader by
/// match rules, analyzes the URL, expands playlists into child tasks and
/// downloads — degrading to the next candidate on failure.
/// </summary>
public sealed class DownloaderTaskExecutor : ITaskExecutor
{
    private readonly DownloaderFactory _factory;
    private readonly ILogger _log;

    /// <summary>Creates a child task (parent, child, referrer) -> child id.</summary>
    private readonly Func<TaskItem, MediaChild, string?, string> _submitChild;

    public DownloaderTaskExecutor(
        DownloaderFactory factory,
        ILogger log,
        Func<TaskItem, MediaChild, string?, string>? submitChild = null)
    {
        _factory = factory;
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
        Exception? lastError = null;

        foreach (var downloader in candidates)
        {
            try
            {
                var analysis = await downloader.AnalyzeAsync(task.Url, cancellationToken);
                task.DownloaderType = downloader.Type;
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
}
