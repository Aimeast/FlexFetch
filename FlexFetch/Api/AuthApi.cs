using System.Security.Claims;
using FlexFetch.Config;
using FlexFetch.Data;
using FlexFetch.Entities;
using FlexFetch.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace FlexFetch.Api;

public sealed record RegisterRequest(string UserName, string Password);

public sealed record LoginRequest(string UserName, string Password);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public static class AuthApi
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/register", (RegisterRequest req, UserService users) =>
        {
            var result = users.Register(req.UserName, req.Password);
            return result switch
            {
                RegisterResult.Success => Results.Ok(),
                RegisterResult.ApprovalPending => Results.Ok(new { pending = true }),
                RegisterResult.UserNameTaken => Results.Conflict(new { error = "Username already taken" }),
                RegisterResult.RegistrationClosed => Results.Forbid(),
                _ => Results.BadRequest(new { error = "Invalid username or password" }),
            };
        });

        group.MapPost("/login", async (LoginRequest req, UserService users, HttpContext ctx) =>
        {
            var outcome = users.Login(req.UserName, req.Password);
            switch (outcome.Status)
            {
                case LoginStatus.InvalidCredentials:
                    return Results.Unauthorized();
                case LoginStatus.NotActive:
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var user = outcome.User!;
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id),
                new Claim(ClaimTypes.Name, user.UserName),
                new Claim(ClaimTypes.Role, user.Role.ToString()),
            };
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity));

            return Results.Ok(new { id = user.Id, userName = user.UserName, role = user.Role.ToString() });
        });

        group.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok();
        });

        // Change the signed-in user's own password. The current password must
        // be confirmed; the session cookie stays valid afterwards.
        group.MapPost("/change-password", (ChangePasswordRequest req, UserService users, HttpContext ctx) =>
        {
            var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
            {
                return Results.Unauthorized();
            }

            return users.ChangePassword(userId, req.CurrentPassword, req.NewPassword) switch
            {
                ChangePasswordResult.Success => Results.Ok(),
                ChangePasswordResult.InvalidCredentials => Results.Json(
                    new { error = "Current password is incorrect" }, statusCode: StatusCodes.Status401Unauthorized),
                ChangePasswordResult.InvalidInput => Results.BadRequest(
                    new { error = "New password must be at least 5 characters" }),
                _ => Results.Unauthorized(),
            };
        }).RequireAuthorization();

        // Public bootstrap info for the web UI: who is signed in and whether
        // anonymous guest access is enabled on this deployment.
        group.MapGet("/status", (HttpContext ctx, IUserRepository users, IConfiguration config) =>
        {
            var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var user = string.IsNullOrEmpty(userId) ? null : users.GetById(userId);
            return Results.Ok(new
            {
                authenticated = user is not null,
                userName = user?.UserName,
                role = user?.Role.ToString(),
                anonymousEnabled = ConfigRegistry.From(config, ConfigKeys.AllowAnonymous)
                    .Equals("true", StringComparison.OrdinalIgnoreCase),
            });
        });

        // Current signed-in user info (name + role), used by the UI to
        // decide which navigation links to show.
        group.MapGet("/me", (HttpContext ctx, IUserRepository users) =>
        {
            var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
            {
                return Results.Unauthorized();
            }

            var user = users.GetById(userId);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(new { id = user.Id, userName = user.UserName, role = user.Role.ToString() });
        }).RequireAuthorization();
    }
}
