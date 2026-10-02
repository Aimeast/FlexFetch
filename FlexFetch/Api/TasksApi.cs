using System.Security.Claims;
using FlexFetch.Config;
using FlexFetch.Data;
using FlexFetch.Entities;
using FlexFetch.Services;
using FlexFetch.Services.Tasks;
using TaskStatus = FlexFetch.Enums.TaskStatus;

namespace FlexFetch.Api;

public sealed record SubmitTaskRequest(string Url);

public static class TasksApi
{
    public static void Map(WebApplication app)
    {
        // AppAccess: authenticated users, plus anonymous guests when the
        // account.allowAnonymous configuration is enabled. Each guest owns a
        // private per-browser session (FlexFetch.Guest cookie); tasks never
        // cross between guests, users, or the two worlds.
        var group = app.MapGroup("/api/tasks").RequireAuthorization("AppAccess");

        group.MapGet("/", (TaskService tasks, GuestSessionService guests, HttpContext ctx) =>
        {
            // Read-only path: no guest session is created for a visitor who
            // only looks; a signed-in user always has an id, a guest without
            // a session sees an empty list.
            var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
            {
                var session = guests.GetExistingOwnerId(ctx);
                if (session is null)
                {
                    return Results.Ok(Array.Empty<TaskItem>());
                }

                userId = session;
            }

            // Newest first: a stable, predictable order (task ids are random
            // high-entropy strings and must never be exposed as a sequence).
            return Results.Ok(tasks.GetByOwner(userId).OrderByDescending(t => t.CreatedAt));
        });

        group.MapPost("/", (SubmitTaskRequest req, TaskService tasks, GuestSessionService guests, HttpContext ctx) =>
        {
            if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return Results.BadRequest(new { error = "Only http/https URLs are accepted" });
            }

            var id = tasks.Submit(GetOwnerId(guests, ctx), req.Url);
            return Results.Ok(new { id });
        });

        group.MapDelete("/{id}", (string id, TaskService tasks, GuestSessionService guests, HttpContext ctx) =>
        {
            var task = tasks.GetById(id);
            if (task is null || task.OwnerUserId != GetOwnerId(guests, ctx))
            {
                return Results.NotFound();
            }

            // Deleting a running task terminates it (FR-1.3: delete stops and cleans up).
            return tasks.Delete(id) ? Results.Ok() : Results.NotFound();
        });

        group.MapPost("/{id}/retry", (string id, TaskService tasks, GuestSessionService guests, HttpContext ctx) =>
        {
            var task = tasks.GetById(id);
            if (task is null || task.OwnerUserId != GetOwnerId(guests, ctx))
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
            GuestSessionService guests,
            HttpContext ctx) =>
        {
            var task = tasks.GetById(id);
            if (task is null || task.OwnerUserId != GetOwnerId(guests, ctx))
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
            // Media is served with an inline disposition carrying the real
            // name: the browser plays it (RFC 6266 - inline filenames never
            // influence rendering) and uses that same name for "save link
            // as" instead of deriving "file.mp4" from the generic URL.
            // Other types keep the attachment name and download.
            var mime = FileMime.For(task.FileName);
            if (FileMime.IsMedia(task.FileName))
            {
                var disposition = FileMime.InlineDispositionFor(task.FileName);
                if (disposition is not null)
                {
                    ctx.Response.Headers.ContentDisposition = disposition;
                }
                return Results.File(stream, mime, enableRangeProcessing: true);
            }
            return Results.File(stream, mime, task.FileName, enableRangeProcessing: true);
        });

        group.MapPost("/{id}/share", (string id, TaskService tasks, IShareRepository shares, IConfiguration config, GuestSessionService guests, HttpContext ctx) =>
        {
            var task = tasks.GetById(id);
            if (task is null || task.OwnerUserId != GetOwnerId(guests, ctx))
            {
                return Results.NotFound();
            }

            // Reuse the existing non-expired share so the link stays stable
            // across clicks; only create a new token when there is none.
            var now = DateTime.UtcNow;
            var existing = shares.GetByTaskId(id).FirstOrDefault(s =>
                s.ExpiresAt is null || s.ExpiresAt.Value.ToUniversalTime() > now);
            if (existing is not null)
            {
                return Results.Ok(new { token = existing.Token, expiresAt = existing.ExpiresAt });
            }

            var hours = int.TryParse(ConfigRegistry.From(config, ConfigKeys.ShareTokenHours), out var h) ? h : 0;
            var share = new ShareToken
            {
                TaskId = id,
                ExpiresAt = hours > 0 ? DateTime.UtcNow.AddHours(hours) : null,
            };
            shares.Insert(share);
            return Results.Ok(new { token = share.Token, expiresAt = share.ExpiresAt });
        });
    }

    // Owner id for the request: the signed-in user's id, or the guest
    // session id (creating the session and cookie on first use). Mutating
    // paths must go through this so guest sessions stay alive.
    private static string GetOwnerId(GuestSessionService guests, HttpContext ctx) =>
        ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } id
            ? id
            : guests.GetOrCreateOwnerId(ctx);
}
