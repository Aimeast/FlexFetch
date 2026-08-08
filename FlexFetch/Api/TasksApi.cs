using System.Security.Claims;
using FlexFetch.Data;
using FlexFetch.Entities;
using FlexFetch.Services;
using FlexFetch.Services.Tasks;
using Microsoft.AspNetCore.Authorization;
using TaskStatus = FlexFetch.Enums.TaskStatus;

namespace FlexFetch.Api;

public sealed record SubmitTaskRequest(string Url);

public static class TasksApi
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/tasks").RequireAuthorization();

        group.MapGet("/", (TaskService tasks, HttpContext ctx) =>
        {
            var userId = GetUserId(ctx);
            // Newest first: a stable, predictable order (task ids are random
            // high-entropy strings and must never be exposed as a sequence).
            return Results.Ok(tasks.GetByOwner(userId).OrderByDescending(t => t.CreatedAt));
        });

        group.MapPost("/", (SubmitTaskRequest req, TaskService tasks, HttpContext ctx) =>
        {
            if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return Results.BadRequest(new { error = "Only http/https URLs are accepted" });
            }

            var id = tasks.Submit(GetUserId(ctx), req.Url);
            return Results.Ok(new { id });
        });

        group.MapDelete("/{id}", (string id, TaskService tasks, HttpContext ctx) =>
        {
            var task = tasks.GetById(id);
            if (task is null || task.OwnerUserId != GetUserId(ctx))
            {
                return Results.NotFound();
            }

            // Deleting a running task terminates it (FR-1.3: delete stops and cleans up).
            return tasks.Delete(id) ? Results.Ok() : Results.NotFound();
        });

        group.MapPost("/{id}/retry", (string id, TaskService tasks, HttpContext ctx) =>
        {
            var task = tasks.GetById(id);
            if (task is null || task.OwnerUserId != GetUserId(ctx))
            {
                return Results.NotFound();
            }
            if (task.Status == TaskStatus.Running)
            {
                return Results.Conflict(new { error = "Task is running" });
            }

            return tasks.Retry(id) ? Results.Ok() : Results.Conflict(new { error = "Task cannot be retried" });
        });

        group.MapGet("/{id}/file", (
            string id,
            TaskService tasks,
            StorageService storage,
            HttpContext ctx) =>
        {
            var task = tasks.GetById(id);
            if (task is null || task.OwnerUserId != GetUserId(ctx))
            {
                return Results.NotFound();
            }
            if (task.Status != TaskStatus.Completed || string.IsNullOrEmpty(task.FileName))
            {
                return Results.Conflict(new { error = "Task is not completed yet" });
            }

            var path = storage.GetTaskFilePath(task.Id, task.FileName);
            if (!File.Exists(path))
            {
                return Results.NotFound();
            }

            var stream = File.OpenRead(path);
            // Media (video/audio) is served without a download name so the
            // browser plays it inline; other types keep the name and download.
            var mime = FileMime.For(task.FileName);
            return FileMime.IsMedia(task.FileName)
                ? Results.File(stream, mime, enableRangeProcessing: true)
                : Results.File(stream, mime, task.FileName, enableRangeProcessing: true);
        });

        group.MapPost("/{id}/share", (string id, TaskService tasks, IShareRepository shares, IConfigRepository config, HttpContext ctx) =>
        {
            var task = tasks.GetById(id);
            if (task is null || task.OwnerUserId != GetUserId(ctx))
            {
                return Results.NotFound();
            }

            var hours = int.TryParse(config.Get(FlexFetch.Config.ConfigKeys.ShareTokenHours), out var h) ? h : 0;
            var share = new ShareToken
            {
                TaskId = id,
                ExpiresAt = hours > 0 ? DateTime.UtcNow.AddHours(hours) : null,
            };
            shares.Insert(share);
            return Results.Ok(new { token = share.Token, expiresAt = share.ExpiresAt });
        });
    }

    internal static string GetUserId(HttpContext ctx) =>
        ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
}
