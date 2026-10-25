using FlexFetch.Data;
using FlexFetch.Enums;
using FlexFetch.Services;
using FlexFetch.Services.Tasks;

namespace FlexFetch.Api;

public static class UsersApi
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/users").RequireAuthorization("Admin");

        group.MapGet("/pending", (UserService users) => Results.Ok(users.GetPending()));

        group.MapPost("/{id}/approve", (string id, UserService users) =>
            users.Approve(id) ? Results.Ok() : Results.NotFound());

        group.MapPost("/{id}/disable", (string id, UserService users) =>
        {
            var user = users.GetById(id);
            if (user is null)
            {
                return Results.NotFound();
            }

            if (IsLastActiveAdmin(users, user))
            {
                return Results.Conflict(new { error = "Cannot disable the last active admin" });
            }

            return users.Disable(id) ? Results.Ok() : Results.NotFound();
        });

        group.MapDelete("/{id}", (string id, UserService users, ITaskRepository tasks, TaskService taskService) =>
        {
            var user = users.GetById(id);
            if (user is null)
            {
                return Results.NotFound();
            }

            if (IsLastActiveAdmin(users, user))
            {
                return Results.Conflict(new { error = "Cannot delete the last active admin" });
            }

            // The account owns its downloads: remove the whole task groups
            // (files and share tokens included) instead of leaving publicly
            // reachable orphans behind.
            foreach (var task in tasks.GetByOwner(id).Where(t => t.ParentId is null))
            {
                taskService.Delete(task.Id);
            }

            return users.Delete(id) ? Results.Ok() : Results.NotFound();
        });
    }

    private static bool IsLastActiveAdmin(UserService users, FlexFetch.Entities.User user) =>
        user.Role == UserRole.Admin
        && user.Status == UserStatus.Active
        && users.GetAll().Count(u => u.Role == UserRole.Admin && u.Status == UserStatus.Active) <= 1;
}
