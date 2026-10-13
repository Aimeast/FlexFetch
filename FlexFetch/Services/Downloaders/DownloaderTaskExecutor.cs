using FlexFetch.Data;
using FlexFetch.Entities;
using FlexFetch.Services.Routing;
using FlexFetch.Services.Tasks;
using ILogger = Serilog.ILogger;
using TaskStatus = FlexFetch.Enums.TaskStatus;

namespace FlexFetch.Services.Downloaders;

/// <summary>
/// Bridges TaskService and the downloader plugins: selects a downloader by
/// match rules, analyzes the URL, expands playlists into child tasks and
/// downloads - degrading to the next candidate on failure. URLs the server
/// answers with a file (a Content-Disposition name, or a MIME type that
/// maps to an extension) are handed to the generic file downloader right
/// away instead of wasting a slow yt-dlp attempt.
/// </summary>
public sealed class DownloaderTaskExecutor : ITaskExecutor
{
    private readonly DownloaderFactory _factory;
    private readonly IProxyService _proxy;
    private readonly ITaskRepository _tasks;
    private readonly StorageService _storage;
    private readonly ILogger _log;

    /// <summary>Creates a child task (parent, child, referrer) -> child id.</summary>
    private readonly Func<TaskItem, MediaChild, string?, string> _submitChild;

    /// <summary>The server probe behind direct-file routing (injectable in tests).</summary>
    private readonly Func<string, IProxyService, CancellationToken, Task<DirectLinkDetector.DirectLinkProbe?>> _probe;

    public DownloaderTaskExecutor(
        DownloaderFactory factory,
        IProxyService proxy,
        ITaskRepository tasks,
        StorageService storage,
        ILogger log,
        Func<TaskItem, MediaChild, string?, string>? submitChild = null,
        Func<string, IProxyService, CancellationToken, Task<DirectLinkDetector.DirectLinkProbe?>>? probe = null)
    {
        _factory = factory;
        _proxy = proxy;
        _tasks = tasks;
        _storage = storage;
        _log = log;
        _submitChild = submitChild
            ?? ((_, _, _) => throw new InvalidOperationException("Child task submission is not configured"));
        _probe = probe ?? DirectLinkDetector.ProbeAsync;
    }

    public async Task<TaskExecutionResult> ExecuteAsync(
        TaskItem task,
        Action<double> progress,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<IDownloader> candidates;
        if (!string.IsNullOrWhiteSpace(task.DownloaderType))
        {
            // The task's downloader was already resolved (e.g. a child task
            // whose parent analysis pinned the Generic downloader for a direct
            // media stream). Use exactly that downloader instead of
            // re-negotiating through URL matching / Content-Type probing / the
            // generic fallback chain (which would open another browser tab).
            candidates = _factory.All.Where(d => d.Type == task.DownloaderType).ToList();
            if (candidates.Count == 0)
            {
                throw new InvalidOperationException($"Downloader {task.DownloaderType} is not registered");
            }
        }
        else
        {
            candidates = _factory.SelectDownloaders(task.Url);
            candidates = await PromoteDirectFileAsync(candidates, task.Url, cancellationToken);
        }

        Exception? lastError = null;

        foreach (var downloader in candidates)
        {
            try
            {
                var analysis = await downloader.AnalyzeAsync(task.Url, task.Id, cancellationToken);
                task.DownloaderType = downloader.Type;
                task.ContentText = string.IsNullOrWhiteSpace(analysis.ContentText) ? task.ContentText : analysis.ContentText;
                // The title names the storage folder (files/{id} {title prefix});
                // the folder is stamped once and never renamed afterwards.
                task.Title ??= analysis.Title;
                task.StorageFolder ??= ResolveStorageFolder(task, analysis);
                _log.Information("Task {TaskId}: using downloader {Type}", task.Id, downloader.Type);

                if (analysis.Children.Count > 0)
                {
                    foreach (var child in analysis.Children)
                    {
                        _submitChild(task, child, analysis.Referrer);
                    }

                    return TaskExecutionResult.Expanded;
                }

                analysis.SuggestedFileName = EnsureGroupUniqueName(task, analysis.SuggestedFileName);
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
    /// The folder under files/ holding this task's file: the parent group's
    /// folder for children (the parent record already carries its stamped
    /// folder - children run only after the parent's expansion), its own
    /// "{id} {title prefix}" for roots.
    /// </summary>
    private string ResolveStorageFolder(TaskItem task, AnalysisResult analysis)
    {
        if (task.ParentId is not null)
        {
            var parent = _tasks.GetById(task.ParentId);
            return parent?.StorageFolder ?? task.ParentId;
        }

        return StorageService.BuildFolderName(task.Id, task.Title ?? analysis.Title);
    }

    /// <summary>
    /// Keeps file names unique inside a group folder. Children of one list
    /// normally have distinct names (each child's own analysis resolves the
    /// name), but same-titled entries exist (browser/HTML expansion shares
    /// one title). On a clash both files end up carrying their task id; the
    /// finished sibling's file is renamed with it, and the new download is
    /// suffixed before it starts. A file left by THIS task's earlier attempt
    /// is not a clash - a retry overwrites its own leftover.
    /// </summary>
    private string EnsureGroupUniqueName(TaskItem task, string? suggestedName)
    {
        if (string.IsNullOrWhiteSpace(suggestedName) || task.ParentId is null)
        {
            return suggestedName ?? string.Empty;
        }

        var clash = _tasks.GetChildren(task.ParentId)
            .FirstOrDefault(s => s.Id != task.Id && s.FileName == suggestedName);
        if (clash is null)
        {
            return suggestedName;
        }

        if (clash.Status == TaskStatus.Completed && task.StorageFolder is not null)
        {
            try
            {
                var renamed = FileNameRules.SuffixWithId(suggestedName, clash.Id);
                var previous = _storage.GetTaskFilePath(task.StorageFolder, suggestedName);
                if (File.Exists(previous))
                {
                    File.Move(previous, _storage.GetTaskFilePath(task.StorageFolder, renamed));
                }

                clash.FileName = renamed;
                _tasks.Update(clash);
                _log.Information("Task {TaskId}: file name {Name} clashed with task {ClashId}; both files now carry their ids",
                    task.Id, suggestedName, clash.Id);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Task {TaskId}: renaming clashed file of task {ClashId} failed",
                    task.Id, clash.Id);
            }
        }

        return FileNameRules.SuffixWithId(suggestedName, task.Id);
    }

    /// <summary>
    /// Asks the server what it serves (HEAD, falling back to a one-byte
    /// Range GET) and promotes the generic file downloader to the front when
    /// the answer is a file: the response carries a Content-Disposition name,
    /// or the Content-Type maps to an extension in the MIME table. The file
    /// is then named and downloaded straight away; handing such links to
    /// yt-dlp first made its generic extractor rename them after its
    /// "unknown_video" placeholder instead. Pages (text/html and friends,
    /// absent from the MIME table), HLS/DASH manifests, probe failures and
    /// domain-specific matches keep the normal chain.
    /// </summary>
    private async Task<IReadOnlyList<IDownloader>> PromoteDirectFileAsync(
        IReadOnlyList<IDownloader> candidates,
        string url,
        CancellationToken cancellationToken)
    {
        // Domain-specific matchers claim their URLs alone; a probe would be
        // a wasted round trip on every video page.
        if (candidates.Count == 0 || candidates[0].IsDomainSpecific || !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            return candidates;
        }

        var probe = await _probe(url, _proxy, cancellationToken);
        if (probe is null
            || DirectLinkDetector.IsManifestContentType(probe.ContentType)
            || DirectLinkDetector.IsManifestFileName(probe.FileName))
        {
            return candidates;
        }

        // A file either way: the server named it, or its type maps to an
        // extension (video/mp4 on an extension-less URL, a PDF, an ISO).
        // Unmapped types (HTML pages among them) belong to the extractors.
        if (string.IsNullOrWhiteSpace(probe.FileName) && MimeTypes.ExtensionFor(probe.ContentType) is null)
        {
            return candidates;
        }

        var generic = candidates.FirstOrDefault(d => d.Type == "Generic");
        if (generic is null || candidates[0] == generic)
        {
            return candidates;
        }

        _log.Information("Direct file link detected (type {ContentType}, advertised name {Name})",
            probe.ContentType, probe.FileName ?? "none");
        return new[] { generic }.Concat(candidates.Where(d => d != generic)).ToList();
    }
}
