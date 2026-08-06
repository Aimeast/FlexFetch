using System.Security.Claims;
using FlexFetch.Entities;
using FlexFetch.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace FlexFetch.Api;

public sealed record RegisterRequest(string UserName, string Password);

public sealed record LoginRequest(string UserName, string Password);

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
    }
}
