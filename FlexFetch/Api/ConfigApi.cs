using FlexFetch.Config;
using FlexFetch.Data;

namespace FlexFetch.Api;

/// <summary>
/// Admin-only configuration endpoints: list all declared config items with
/// current values, and update individual keys (validated against the registry).
/// </summary>
public static class ConfigApi
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/config").RequireAuthorization("Admin");

        group.MapGet("/", (IConfigRepository repo) =>
        {
            var current = repo.GetAll();
            var items = ConfigRegistry.All
                .Select(item => new
                {
                    item.Key,
                    item.Category,
                    defaultValue = item.DefaultValue,
                    value = current.GetValueOrDefault(item.Key) ?? item.DefaultValue,
                })
                .OrderBy(i => i.Key)
                .ToList();
            return Results.Ok(items);
        });

        group.MapPut("/", (UpdateConfigRequest req, IConfigRepository repo) =>
        {
            var error = ConfigRegistry.Validate(req.Key, req.Value);
            if (error is not null)
            {
                return Results.BadRequest(new { error });
            }

            repo.Set(req.Key, req.Value);
            return Results.Ok();
        });
    }

    public sealed record UpdateConfigRequest(string Key, string Value);
}
