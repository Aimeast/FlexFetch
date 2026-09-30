namespace FlexFetch.Config;

/// <summary>
/// Central configuration registry: every config item is declared once here
/// with its default value and validator. The registry is the single source
/// of truth for the set of known configuration keys.
/// </summary>
public static class ConfigRegistry
{
    private static readonly IReadOnlyDictionary<string, ConfigItem> Items = BuildItems();

    public static IReadOnlyCollection<ConfigItem> All => Items.Values.ToList();

    public static bool ContainsKey(string key) => Items.ContainsKey(key);

    public static ConfigItem Get(string key) =>
        Items.TryGetValue(key, out var item)
            ? item
            : throw new KeyNotFoundException($"Unknown configuration key: {key}");

    public static string GetDefault(string key) => Get(key).DefaultValue;

    /// <summary>
    /// Reads a config value from the appsettings configuration (keys are
    /// dot-separated here, colons in IConfiguration), falling back to the
    /// registry default when absent.
    /// </summary>
    public static string From(IConfiguration configuration, string key) =>
        configuration[key.Replace('.', ':')] ?? GetDefault(key);

    /// <summary>Validates a value for the given key; returns an error message or null.</summary>
    public static string? Validate(string key, string value) =>
        Items.TryGetValue(key, out var item) ? item.Validator(value) : $"Unknown configuration key: {key}";

    private static IReadOnlyDictionary<string, ConfigItem> BuildItems()
    {
        var items = new List<ConfigItem>
        {
            new()
            {
                Key = ConfigKeys.RegistrationPolicy,
                DefaultValue = "Open",
                Validator = v => Enum.TryParse<RegistrationPolicy>(v, true, out _) ? null : "Must be Open, Closed or Approval",
            },
            new()
            {
                Key = ConfigKeys.SessionHours,
                DefaultValue = "168",
                Validator = PositiveInt("Must be a positive number of hours"),
            },
            new()
            {
                Key = ConfigKeys.InactiveDays,
                DefaultValue = "30",
                Validator = NonNegativeInt("Must be a non-negative number of days (0 disables cleanup)"),
            },
            new()
            {
                // When enabled, visitors without an account can use the task
                // API through per-browser guest sessions (read, submit,
                // download, delete). Account management stays signed-in only.
                Key = ConfigKeys.AllowAnonymous,
                DefaultValue = "false",
                Validator = BoolValidator("Must be true or false"),
            },
            new()
            {
                // Idle lifetime of a guest session: when the cleanup sweep
                // finds a session idle beyond this many hours (and with no
                // tasks still queued/running), the session and all its tasks
                // and files are deleted. Default: 15 days.
                Key = ConfigKeys.AnonymousSessionHours,
                DefaultValue = "360",
                Validator = PositiveInt("Must be a positive number of hours"),
            },
            new()
            {
                Key = ConfigKeys.MaxConcurrency,
                DefaultValue = "2",
                Validator = PositiveInt("Must be a positive concurrency count"),
            },
            new()
            {
                Key = ConfigKeys.MaxRetries,
                DefaultValue = "3",
                Validator = NonNegativeInt("Must be a non-negative retry count"),
            },
            new()
            {
                Key = ConfigKeys.TimeoutSeconds,
                DefaultValue = "60",
                Validator = PositiveInt("Must be a positive number of seconds"),
            },
            new()
            {
                Key = ConfigKeys.Proxy,
                DefaultValue = string.Empty,
                Validator = v => string.IsNullOrWhiteSpace(v) || Uri.TryCreate(v.StartsWith("socks5") ? v : $"http://{v}", UriKind.Absolute, out _)
                    ? null
                    : "Must be a valid proxy URL (http/https/socks5) or empty",
            },
            new()
            {
                Key = ConfigKeys.NetworkHttpProxy,
                // Optional dedicated http proxy for installer child processes
                // (deno install, Playwright browser download): they cannot use
                // a socks5 proxy. Empty = installers run direct/mirrored.
                DefaultValue = string.Empty,
                Validator = v => string.IsNullOrWhiteSpace(v) || (Uri.TryCreate(v, UriKind.Absolute, out var u)
                    && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
                    ? null
                    : "Must be an absolute http/https proxy URL or empty",
            },
            new()
            {
                Key = ConfigKeys.RouteRules,
                DefaultValue = string.Empty,
                Validator = _ => null,
            },
            new()
            {
                Key = ConfigKeys.DefaultAction,
                DefaultValue = "UseProxy",
                Validator = v => v is "UseProxy" or "Direct" ? null : "Must be UseProxy or Direct",
            },
            new()
            {
                Key = ConfigKeys.SessionPeriodHours,
                DefaultValue = "12",
                Validator = PositiveInt("Must be a positive number of hours"),
            },
            new()
            {
                Key = ConfigKeys.SessionCanaryReexportThrottleHours,
                DefaultValue = "1",
                Validator = PositiveInt("Must be a positive number of hours"),
            },
            new()
            {
                Key = ConfigKeys.SessionCanaryFailureThreshold,
                DefaultValue = "3",
                Validator = PositiveInt("Must be a positive failure count"),
            },
            new()
            {
                Key = ConfigKeys.SessionProbeUrl,
                DefaultValue = "https://www.youtube.com/watch?v=aqz-KE-bpKQ",
                Validator = v => Uri.TryCreate(v, UriKind.Absolute, out _) ? null : "Must be an absolute URL",
            },
            new()
            {
                Key = ConfigKeys.SessionHumanize,
                DefaultValue = "true",
                Validator = BoolValidator("Must be true or false"),
            },
            new()
            {
                Key = ConfigKeys.AutoUpgrade,
                DefaultValue = "true",
                Validator = BoolValidator("Must be true or false"),
            },
            new()
            {
                Key = ConfigKeys.UpgradeHours,
                DefaultValue = "24",
                Validator = PositiveInt("Must be a positive number of hours"),
            },
            new()
            {
                Key = ConfigKeys.AutoInstallDeps,
                DefaultValue = "true",
                Validator = BoolValidator("Must be true or false"),
            },
            new()
            {
                Key = ConfigKeys.DataDir,
                DefaultValue = ".flexfetch",
                Validator = v => string.IsNullOrWhiteSpace(v) ? "Must not be empty" : null,
            },
            new()
            {
                Key = ConfigKeys.ShareTokenHours,
                DefaultValue = "0",
                Validator = NonNegativeInt("Must be a non-negative number of hours (0 means no expiry)"),
            },
        };

        return items.ToDictionary(i => i.Key);
    }

    private static Func<string, string?> PositiveInt(string error) =>
        v => int.TryParse(v, out var n) && n > 0 ? null : error;

    private static Func<string, string?> NonNegativeInt(string error) =>
        v => int.TryParse(v, out var n) && n >= 0 ? null : error;

    private static Func<string, string?> BoolValidator(string error) =>
        v => bool.TryParse(v, out _) ? null : error;
}

/// <summary>Account registration policy.</summary>
public enum RegistrationPolicy
{
    Open = 0,
    Closed = 1,
    Approval = 2,
}
