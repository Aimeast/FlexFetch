namespace FlexFetch.Config;

/// <summary>
/// Central registry of configuration keys. A new config item is declared
/// in exactly one place and is automatically exposed by the API.
/// </summary>
public static class ConfigKeys
{
    // Account
    public const string RegistrationPolicy = "account.registrationPolicy";
    public const string SessionHours = "account.sessionHours";
    public const string InactiveDays = "account.inactiveDays";
    public const string AllowAnonymous = "account.allowAnonymous";
    public const string AnonymousSessionHours = "account.anonymousSessionHours";

    // Download
    public const string MaxConcurrency = "download.maxConcurrency";
    public const string MaxRetries = "download.maxRetries";
    public const string TimeoutSeconds = "download.timeoutSeconds";

    // Network
    public const string Proxy = "network.proxy";
    public const string NetworkHttpProxy = "network.httpProxy";
    public const string RouteRules = "network.routeRules";
    public const string DefaultAction = "network.defaultAction";

    // Session (YouTube login-session maintenance). There is no master
    // switch: importing a session snapshot opts the deployment in. One
    // period drives both gears - a young snapshot gets a cheap health
    // check, an aged snapshot gets the full export pipeline.
    public const string SessionPeriodHours = "session.periodHours";
    public const string SessionCanaryReexportThrottleHours = "session.canaryReexportThrottleHours";
    public const string SessionCanaryFailureThreshold = "session.canaryFailureThreshold";
    public const string SessionProbeUrl = "session.probeUrl";
    public const string SessionHumanize = "session.humanizeEnabled";

    // PO token provider (bgutil script-deno mode) is always active: it is
    // installed automatically by the supervisor and required for the mweb
    // session posture on flagged IPs.

    // Operations
    public const string AutoUpgrade = "ops.autoUpgradeEnabled";
    public const string UpgradeHours = "ops.upgradePeriodHours";
    public const string AutoInstallDeps = "ops.autoInstallDeps";

    // Storage
    public const string DataDir = "storage.dataDir";

    // Security
    public const string ShareTokenHours = "security.shareTokenHours";
}
