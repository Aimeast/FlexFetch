namespace FlexFetch.Enums;

/// <summary>Refresh state of a cookie group (browser refresh cycle).</summary>
public enum CookieRefreshStatus
{
    /// <summary>Never refreshed yet.</summary>
    Never = 0,

    /// <summary>Refresh in progress right now.</summary>
    Running = 1,

    /// <summary>Last refresh completed successfully.</summary>
    Ok = 2,

    /// <summary>Last refresh failed.</summary>
    Failed = 3,
}
