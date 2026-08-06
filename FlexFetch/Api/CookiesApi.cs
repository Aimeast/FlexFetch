using FlexFetch.Services;

namespace FlexFetch.Api;

public sealed record ImportCookiesRequest(string? Text, string? GroupName, string? Url);

/// <summary>
/// Admin-only cookie pool endpoints: read groups, import cookies from
/// pasted text (Netscape or Set-Cookie), files or response headers.
/// </summary>
public static class CookiesApi
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/cookies").RequireAuthorization("Admin");

        group.MapGet("/", (CookiePoolService pool) => Results.Ok(pool.GetGroups()));

        group.MapPost("/import", (ImportCookiesRequest req, CookiePoolService pool) =>
        {
            if (string.IsNullOrWhiteSpace(req.Text))
            {
                return Results.BadRequest(new { error = "Text is required" });
            }

            CookieImportResult result;
            if (!string.IsNullOrWhiteSpace(req.Url) && Uri.TryCreate(req.Url, UriKind.Absolute, out var uri))
            {
                result = pool.ImportText(uri, req.Text, req.GroupName);
            }
            else
            {
                result = pool.ImportNetscape(req.Text, req.GroupName);
            }

            return Results.Ok(result);
        });

        group.MapPost("/groups", (CookieGroupDto dto, CookiePoolService pool) =>
        {
            if (string.IsNullOrWhiteSpace(dto.Name))
            {
                return Results.BadRequest(new { error = "Group name is required" });
            }

            var group = pool.CreateGroup(dto.Name, dto.Urls ?? Array.Empty<string>());
            return Results.Ok(new { id = group.Id });
        });

        group.MapDelete("/groups/{id}", (string id, CookiePoolService pool) =>
            pool.DeleteGroup(id) ? Results.Ok() : Results.NotFound());
    }

    public sealed record CookieGroupDto(string Name, IReadOnlyList<string>? Urls);
}
