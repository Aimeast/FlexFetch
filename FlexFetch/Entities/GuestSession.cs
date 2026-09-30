namespace FlexFetch.Entities;

/// <summary>
/// An anonymous visitor session: the server-side record backing the guest
/// cookie. Tasks submitted by the guest are owned by the session id, and the
/// session - together with all its tasks and files - is deleted by the
/// cleanup service once it has been idle beyond the configured threshold.
/// </summary>
public sealed class GuestSession
{
    /// <summary>Session and task-owner id; carries the "guest-" prefix so it can never collide with a real user id.</summary>
    public string Id { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime LastActiveAt { get; set; } = DateTime.UtcNow;
}
