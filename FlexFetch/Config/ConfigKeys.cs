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

    // Download
    public const string MaxConcurrency = "download.maxConcurrency";
    public const string MaxRetries = "download.maxRetries";
    public const string TimeoutSeconds = "download.timeoutSeconds";

    // Network
    public const string Proxy = "network.proxy";
    public const string BypassCidrFile = "network.bypassCidrFile";
    public const string RoutePolicies = "network.routePolicies";

    // Cookie pool
    public const string CookieAutoRefresh = "cookie.autoRefreshEnabled";
    public const string CookieRefreshHours = "cookie.refreshPeriodHours";
    public const string CookieRefreshRandomize = "cookie.refreshRandomize";

    // Browser
    public const string BrowserUseSystem = "browser.useSystemBrowser";
    public const string BrowserStealthSelfCheck = "browser.stealthSelfCheck";
    public const string BrowserHumanize = "browser.humanizeEnabled";

    // Operations
    public const string AutoUpgrade = "ops.autoUpgradeEnabled";
    public const string UpgradeHours = "ops.upgradePeriodHours";
    public const string AutoInstallDeps = "ops.autoInstallDeps";

    // Logging
    public const string LogLevel = "logging.level";
    public const string LogOutput = "logging.output";

    // Storage
    public const string DataDir = "storage.dataDir";

    // Security
    public const string ShareTokenHours = "security.shareTokenHours";
    public const string HttpsEnabled = "security.httpsEnabled";
    public const string EnableCompression = "security.enableCompression";
}
