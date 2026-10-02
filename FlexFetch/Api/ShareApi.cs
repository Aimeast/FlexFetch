using System.Net.Http.Headers;
using FlexFetch.Data;
using FlexFetch.Entities;
using FlexFetch.Services;
using TaskStatus = FlexFetch.Enums.TaskStatus;

namespace FlexFetch.Api;

/// <summary>
/// Public read-only share endpoints: view task info by token, download the
/// completed file. The download filename is transmitted with RFC 5987
/// encoding so non-ASCII titles survive.
/// </summary>
public static class ShareApi
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/share");

        group.MapGet("/{token}", (string token, IShareRepository shares, ITaskRepository tasks) =>
        {
            var share = shares.GetByToken(token);
            if (share is null || share.ExpiresAt is not null && share.ExpiresAt.Value.ToUniversalTime() <= DateTime.UtcNow)
            {
                return Results.NotFound();
            }

            // A shared task may be a group (parent) whose files live on its
            // children; resolve all tasks that actually carry a file so the
            // share page can list them like the task list does.
            var files = ResolveShareFiles(tasks, share.TaskId);
            if (files.Count == 0)
            {
                return Results.NotFound();
            }

            var first = files[0];
            var sourceUrl = tasks.GetById(share.TaskId)?.Url ?? first.Url;
            return Results.Ok(new
            {
                url = sourceUrl,
                fileName = first.FileName,
                fileSize = first.FileSize,
                status = first.Status.ToString(),
                progress = first.Progress,
                errorMessage = first.ErrorMessage,
                files = files.Select(f => new
                {
                    id = f.Id,
                    fileName = f.FileName,
                    fileSize = f.FileSize,
                    status = f.Status.ToString(),
                    progress = f.Progress,
                    errorMessage = f.ErrorMessage,
                }),
            });
        });

        group.MapGet("/{token}/file", async (
            string token,
            IShareRepository shares,
            ITaskRepository tasks,
            StorageService storage,
            HttpRequest request) =>
        {
            var share = shares.GetByToken(token);
            if (share is null || share.ExpiresAt is not null && share.ExpiresAt.Value.ToUniversalTime() <= DateTime.UtcNow)
            {
                return Results.NotFound();
            }

            var files = ResolveShareFiles(tasks, share.TaskId);
            if (files.Count == 0)
            {
                return Results.NotFound();
            }

            // An optional taskId selects one file of a shared group; without
            // it the first file (or the task's own) is served.
            var taskId = request.Query["taskId"].FirstOrDefault();
            var task = taskId is null
                ? files[0]
                : files.FirstOrDefault(f => f.Id == taskId);
            if (task is null)
            {
                return Results.NotFound();
            }
            if (task.Status != TaskStatus.Completed)
            {
                return Results.Conflict(new { error = "Task is not completed yet" });
            }
            if (string.IsNullOrEmpty(task.FileName))
            {
                return Results.NotFound();
            }

            var path = storage.GetTaskFilePath(task.Id, task.FileName);
            if (!File.Exists(path))
            {
                return Results.NotFound();
            }

            var stream = File.OpenRead(path);
            // Media is served with an inline disposition carrying the real
            // name (same contract as the tasks file endpoint): the browser
            // plays it inline in a new tab and uses that name for "save
            // link as"; other types keep the attachment name and download.
            var mime = FileMime.For(task.FileName);
            if (FileMime.IsMedia(task.FileName))
            {
                var disposition = FileMime.InlineDispositionFor(task.FileName);
                if (disposition is not null)
                {
                    request.HttpContext.Response.Headers.ContentDisposition = disposition;
                }
                return Results.File(stream, mime, enableRangeProcessing: true);
            }
            return Results.File(stream, mime, task.FileName, enableRangeProcessing: true);
        });
    }

    /// <summary>
    /// Resolves the tasks behind a shared group: the shared task itself when
    /// it carries a file, plus any child tasks that do (a parent of an
    /// expanded video group usually has no file of its own). A task with no
    /// file of its own and no child files is kept itself so the share page
    /// still shows its status and the file endpoint can answer Conflict for
    /// unfinished tasks.
    /// </summary>
    private static IReadOnlyList<TaskItem> ResolveShareFiles(ITaskRepository tasks, string taskId)
    {
        var result = new List<TaskItem>();
        var self = tasks.GetById(taskId);
        if (self is null)
        {
            return result;
        }

        var children = tasks.GetChildren(taskId)
            .Where(c => !string.IsNullOrEmpty(c.FileName))
            .ToList();

        if (!string.IsNullOrEmpty(self.FileName) || children.Count == 0)
        {
            result.Add(self);
        }

        result.AddRange(children);
        return result;
    }
}
