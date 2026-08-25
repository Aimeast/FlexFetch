using FlexFetch.Entities;
using FlexFetch.Enums;
using FlexFetch.Services;
using ILogger = Serilog.ILogger;

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

        group.MapPost("/import", (ImportCookiesRequest req, CookiePoolService pool, ILogger log) =>
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

            log.Information("Cookie import: {Imported} imported, {Skipped} skipped{Errors}",
                result.Imported, result.Skipped,
                result.Errors.Count > 0 ? $", errors: {string.Join("; ", result.Errors)}" : string.Empty);
            return Results.Ok(result);
        });

        group.MapPost("/import-items", (ImportItemsRequest req, CookiePoolService pool, ILogger log) =>
        {
            var cookies = (req.Cookies ?? Array.Empty<CookieEditDto>()).Select(ToItem).ToList();
            var result = pool.ImportItems(cookies, req.GroupName);
            log.Information("Cookie items import: {Imported} imported, {Skipped} skipped{Errors}",
                result.Imported, result.Skipped,
                result.Errors.Count > 0 ? $", errors: {string.Join("; ", result.Errors)}" : string.Empty);
            return Results.Ok(result);
        });

        group.MapPost("/items/delete", (DeleteCookieRequest req, CookiePoolService pool, ILogger log) =>
        {
            var removed = pool.RemoveCookie(req.GroupName, req.Domain, req.Path, req.Name);
            log.Information("Cookie delete: {Domain}{Path}{Name} removed={Removed}",
                req.Domain, req.Path, req.Name, removed);
            return removed
                ? Results.Ok(new { deleted = true })
                : Results.NotFound(new { error = "Cookie not found" });
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

        // Manual refresh: runs in the background so the page can poll
        // /refresh/status and show per-group progress; returns immediately.
        group.MapPost("/refresh", (CookiePoolService pool, StealthBrowserService browser, ILogger log) =>
        {
            if (!pool.TryStartRefresh())
            {
                return Results.Conflict(new { error = "A refresh is already in progress" });
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    // Refresh groups one by one; each group updates its own
                    // status (Running -> Ok/Failed), so the UI shows progress.
                    var groups = pool.GetGroups();
                    log.Information("Manual cookie refresh started ({GroupCount} groups)", groups.Count);
                    var okCount = 0;
                    foreach (var g in groups)
                    {
                        try
                        {
                            await browser.RefreshGroupAsync(g);
                            okCount++;
                        }
                        catch (Exception ex)
                        {
                            log.Warning("Group {Group} refresh failed: {Message}", g.Name, ex.Message);
                        }
                    }

                    log.Information("Manual cookie refresh finished ({Ok}/{GroupCount} groups)", okCount, groups.Count);
                }
                finally
                {
                    pool.EndRefresh();
                }
            });
            return Results.Accepted((string?)null, new { refreshing = true });
        });

        // Per-group refresh status for the cookie page progress display.
        group.MapGet("/refresh/status", (CookiePoolService pool) =>
        {
            var groups = pool.GetGroups();
            var running = groups.FirstOrDefault(g => g.LastRefreshStatus == CookieRefreshStatus.Running);
            return Results.Ok(new
            {
                refreshing = pool.IsRefreshing,
                currentGroup = running?.Name,
                groups = groups.Select(g => new
                {
                    id = g.Id,
                    name = g.Name,
                    status = g.LastRefreshStatus.ToString(),
                    lastRefreshedAt = g.LastRefreshedAt,
                    lastError = g.LastRefreshError,
                }),
            });
        });

        group.MapPut("/groups/{id}", (string id, CookieGroupDto dto, CookiePoolService pool, ILogger log) =>
        {
            var updated = pool.UpdateGroupUrls(id, dto.Urls ?? Array.Empty<string>());
            log.Information("Group URLs update: {Id} urls={Count} updated={Updated}", id, (dto.Urls ?? Array.Empty<string>()).Count, updated);
            return updated
                ? Results.Ok(new { updated = true })
                : Results.NotFound(new { error = "Group not found" });
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
