using FlexFetch.Entities;
using FlexFetch.Enums;
using FlexFetch.Services;

namespace FlexFetch.Api;

public sealed record ImportCookiesRequest(string? Text, string? GroupName, string? Url);

/// <summary>One cookie row as shown/edited in the UI (expirationDate in unix seconds, browser-export style).</summary>
public sealed record CookieEditDto(
    string Domain,
    string Name,
    string Value,
    string Path,
    bool Secure,
    bool HttpOnly,
    long? ExpirationDate,
    string? SameSite = null);

public sealed record ImportItemsRequest(string? GroupName, IReadOnlyList<CookieEditDto>? Cookies);

public sealed record DeleteCookieRequest(string? GroupName, string Domain, string Path, string Name);

/// <summary>
/// Admin-only cookie pool endpoints: read groups, parse or import cookies from
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
                // Without a site URL, still auto-detect JSON / Netscape / Set-Cookie.
                result = pool.ImportText(url: null, req.Text, req.GroupName);
            }

            return Results.Ok(result);
        });

        group.MapPost("/import-items", (ImportItemsRequest req, CookiePoolService pool) =>
        {
            var cookies = (req.Cookies ?? Array.Empty<CookieEditDto>()).Select(ToItem).ToList();
            return Results.Ok(pool.ImportItems(cookies, req.GroupName));
        });

        group.MapPost("/items/delete", (DeleteCookieRequest req, CookiePoolService pool) =>
            pool.RemoveCookie(req.GroupName, req.Domain, req.Path, req.Name)
                ? Results.Ok(new { deleted = true })
                : Results.NotFound(new { error = "Cookie not found" }));

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
            pool.DeleteGroup(id)
                ? Results.Ok(new { deleted = true })
                : Results.NotFound(new { error = "Group not found" }));
    }

    private static CookieItem ToItem(CookieEditDto d) => new()
    {
        Domain = d.Domain,
        Name = d.Name,
        Value = d.Value,
        Path = string.IsNullOrEmpty(d.Path) ? "/" : d.Path,
        Secure = d.Secure,
        HttpOnly = d.HttpOnly,
        SameSite = Enum.TryParse<SameSitePolicy>(d.SameSite, true, out var sameSite)
            ? sameSite
            : SameSitePolicy.Unspecified,
        ExpiresAt = d.ExpirationDate is > 0
            ? DateTimeOffset.FromUnixTimeSeconds(d.ExpirationDate.Value).UtcDateTime
            : null,
    };

    public sealed record CookieGroupDto(string Name, IReadOnlyList<string>? Urls);
}
