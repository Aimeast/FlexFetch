using FlexFetch.Entities;
using FlexFetch.Services;
using TaskStatus = FlexFetch.Enums.TaskStatus;

namespace FlexFetch.Api;

/// <summary>
/// Serves a completed task's stored file. Both the owner endpoint and the
/// public share endpoint use the same contract: the MIME the server declared
/// at download time wins (clients cannot misparse the payload), media plays
/// inline with a disposition carrying the real name (RFC 6266), everything
/// else is an attachment.
/// </summary>
internal static class StoredFileResponder
{
    public static IResult Respond(TaskItem task, StorageService storage, HttpRequest request)
    {
        if (task.Status != TaskStatus.Completed)
        {
            return Results.Conflict(new { error = "Task is not completed yet" });
        }

        if (string.IsNullOrEmpty(task.FileName))
        {
            return Results.NotFound();
        }

        var path = storage.GetTaskFilePath(task, task.FileName);
        if (!File.Exists(path))
        {
            return Results.NotFound();
        }

        var stream = File.OpenRead(path);
        var mime = task.ContentType ?? FileMime.For(task.FileName);
        if (FileMime.IsMediaMime(mime))
        {
            var disposition = FileMime.InlineDispositionFor(task.FileName);
            if (disposition is not null)
            {
                request.HttpContext.Response.Headers.ContentDisposition = disposition;
            }

            return Results.File(stream, mime, enableRangeProcessing: true);
        }

        return Results.File(stream, mime, task.FileName, enableRangeProcessing: true);
    }
}
