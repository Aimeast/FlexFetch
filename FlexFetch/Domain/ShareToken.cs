namespace FlexFetch.Domain;

/// <summary>
/// A share link for a completed download. The token is a high-entropy
/// random value; visitors with the token can view/download the task
/// without logging in (read-only).
/// </summary>
public sealed class ShareToken
{
    public string Token { get; set; } = RandomId.New();

    public string TaskId { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Optional expiry; null means no expiry.</summary>
    public DateTime? ExpiresAt { get; set; }
}
