using FlexFetch.Config;
using FlexFetch.Data;
using FlexFetch.Entities;

namespace FlexFetch.Services;

/// <summary>
/// Server-side backing of anonymous guest sessions. A guest is identified by
/// the FlexFetch.Guest cookie holding the guest session id; the tasks they
/// submit are owned by that id and are visible only to the same browser.
/// Sessions live while they see activity; the cleanup service deletes an
/// idle session together with all its tasks and files.
/// </summary>
public sealed class GuestSessionService
{
    public const string CookieName = "FlexFetch.Guest";

    private const string IdPrefix = "guest-";

    private readonly IGuestRepository _guests;
    private readonly IConfiguration _config;

    public GuestSessionService(IGuestRepository guests, IConfiguration config)
    {
        _guests = guests;
        _config = config;
    }

    /// <summary>Idle lifetime after which a guest session is eligible for cleanup.</summary>
    public TimeSpan IdleThreshold =>
        TimeSpan.FromHours(int.TryParse(
            ConfigRegistry.From(_config, ConfigKeys.AnonymousSessionHours), out var hours) ? hours : 360);

    /// <summary>
    /// Returns the guest owner id for the request, creating the session (and
    /// setting the cookie) when the visitor has none yet. An existing session
    /// is touched, so every mutating call keeps it alive. Read-only paths use
    /// <see cref="GetExistingOwnerId"/> instead so that merely viewing the
    /// page never creates a session.
    /// </summary>
    public string GetOrCreateOwnerId(HttpContext ctx)
    {
        var existing = ResolveExistingId(ctx);
        if (existing is not null)
        {
            _guests.Touch(existing, DateTime.UtcNow);
            return existing;
        }

        var id = IdPrefix + RandomId.New();
        var now = DateTime.UtcNow;
        _guests.Insert(new GuestSession { Id = id, CreatedAt = now, LastActiveAt = now });
        ctx.Response.Cookies.Append(CookieName, id, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            MaxAge = IdleThreshold,
        });
        return id;
    }

    /// <summary>
    /// Returns the guest owner id when the request already carries a live
    /// session cookie, or null otherwise. Never creates a session.
    /// </summary>
    public string? GetExistingOwnerId(HttpContext ctx) => ResolveExistingId(ctx);

    private string? ResolveExistingId(HttpContext ctx)
    {
        var id = ctx.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(id) || !id.StartsWith(IdPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        // A cookie whose session record is gone (expired and cleaned up)
        // resolves to no session: the next GetOrCreate starts a fresh one.
        return _guests.GetById(id) is null ? null : id;
    }
}
