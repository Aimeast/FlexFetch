namespace FlexFetch.Services.Session;

/// <summary>The outcome of the periodic maintenance decision.</summary>
public enum SessionMaintenanceAction
{
    /// <summary>No session exists - maintenance idles.</summary>
    Idle,

    /// <summary>Young snapshot: a light health check on a one-time copy.</summary>
    LightCheck,

    /// <summary>Aged snapshot: the full refresh pipeline (its probe doubles
    /// as the authoritative check and its write resets the cycle).</summary>
    FullRefresh,
}

/// <summary>
/// The two-gear decision of a periodic session check, as a pure function.
/// The age must come from the stored export timestamp, NOT the file mtime:
/// meta-only verdict rewrites would otherwise make every check see a fresh
/// snapshot and starve the full-refresh gear forever.
/// </summary>
public static class SessionMaintenancePlan
{
    public static SessionMaintenanceAction Decide(bool sessionExists, TimeSpan? age, TimeSpan interval)
    {
        if (!sessionExists)
        {
            return SessionMaintenanceAction.Idle;
        }

        return age is { } value && value >= interval
            ? SessionMaintenanceAction.FullRefresh
            : SessionMaintenanceAction.LightCheck;
    }
}
