using FlexFetch.Services;

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
            users.Disable(id) ? Results.Ok() : Results.NotFound());

        group.MapDelete("/{id}", (string id, UserService users) =>
            users.Delete(id) ? Results.Ok() : Results.NotFound());
    }
}
