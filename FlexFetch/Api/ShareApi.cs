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

            var task = tasks.GetById(share.TaskId);
            if (task is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(new
            {
                fileName = task.FileName,
                fileSize = task.FileSize,
                status = task.Status.ToString(),
                progress = task.Progress,
                errorMessage = task.ErrorMessage,
            });
        });

        group.MapGet("/{token}/file", async (
            string token,
            IShareRepository shares,
            ITaskRepository tasks,
            StorageService storage) =>
        {
            var share = shares.GetByToken(token);
            if (share is null || share.ExpiresAt is not null && share.ExpiresAt.Value.ToUniversalTime() <= DateTime.UtcNow)
            {
                return Results.NotFound();
            }

            var task = tasks.GetById(share.TaskId);
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
            return Results.File(stream, "application/octet-stream", task.FileName, enableRangeProcessing: true);
        });
    }
}
