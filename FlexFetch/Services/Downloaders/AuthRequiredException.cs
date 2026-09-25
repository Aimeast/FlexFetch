namespace FlexFetch.Services.Downloaders;

/// <summary>
/// Why a downloader concluded that the resource needs authentication (or is
/// restricted); used to decide whether attaching cookies could help and to
/// surface a precise reason to the user.
/// </summary>
public enum AuthFailureReason
{
    /// <summary>The site demands a signed-in session (e.g. bot check).</summary>
    LoginRequired,

    /// <summary>The resource is private and requires an authorized account.</summary>
    Private,

    /// <summary>The resource is age-restricted.</summary>
    AgeRestricted,

    /// <summary>The resource is members-only.</summary>
    MembersOnly,

    /// <summary>Matched an auth marker but the exact kind is unknown.</summary>
    Unknown,
}

/// <summary>
/// Thrown when a downloader hits an authentication/restriction error after
/// the cookie path has been exhausted (no cookies available, or cookies were
/// attached and the error persisted). The task fails immediately - it is not
/// retried and no further downloader is tried.
/// </summary>
public sealed class AuthRequiredException : Exception
{
    public AuthRequiredException(AuthFailureReason reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    public AuthFailureReason Reason { get; }
}
