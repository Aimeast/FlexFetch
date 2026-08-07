namespace FlexFetch.Config;

/// <summary>
/// Central configuration registry: every config item is declared once here
/// with its category, default value and validator. The registry is the
/// single source of truth for the set of known configuration keys.
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
                Category = "Account",
                DefaultValue = "Open",
                Validator = v => Enum.TryParse<RegistrationPolicy>(v, true, out _) ? null : "Must be Open, Closed or Approval",
            },
            new()
            {
                Key = ConfigKeys.SessionHours,
                Category = "Account",
                DefaultValue = "168",
                Validator = PositiveInt("Must be a positive number of hours"),
            },
            new()
            {
                Key = ConfigKeys.InactiveDays,
                Category = "Account",
                DefaultValue = "30",
                Validator = NonNegativeInt("Must be a non-negative number of days (0 disables cleanup)"),
            },
            new()
            {
                Key = ConfigKeys.MaxConcurrency,
                Category = "Download",
                DefaultValue = "2",
                Validator = PositiveInt("Must be a positive concurrency count"),
            },
            new()
            {
                Key = ConfigKeys.MaxRetries,
                Category = "Download",
                DefaultValue = "3",
                Validator = NonNegativeInt("Must be a non-negative retry count"),
            },
            new()
            {
                Key = ConfigKeys.TimeoutSeconds,
                Category = "Download",
                DefaultValue = "60",
                Validator = PositiveInt("Must be a positive number of seconds"),
            },
            new()
            {
                Key = ConfigKeys.Proxy,
                Category = "Network",
                DefaultValue = string.Empty,
                Validator = v => string.IsNullOrWhiteSpace(v) || Uri.TryCreate(v.StartsWith("socks5") ? v : $"http://{v}", UriKind.Absolute, out _)
                    ? null
                    : "Must be a valid proxy URL (http/https/socks5) or empty",
            },
            new()
            {
                Key = ConfigKeys.RouteRules,
                Category = "Network",
                DefaultValue = string.Empty,
                Validator = _ => null,
            },
            new()
            {
                Key = ConfigKeys.DefaultAction,
                Category = "Network",
                DefaultValue = "UseProxy",
                Validator = v => v is "UseProxy" or "Direct" ? null : "Must be UseProxy or Direct",
            },
            new()
            {
                Key = ConfigKeys.CookieAutoRefresh,
                Category = "Cookie pool",
                DefaultValue = "true",
                Validator = BoolValidator("Must be true or false"),
            },
            new()
            {
                Key = ConfigKeys.CookieRefreshHours,
                Category = "Cookie pool",
                DefaultValue = "24",
                Validator = PositiveInt("Must be a positive number of hours"),
            },
            new()
            {
                Key = ConfigKeys.CookieRefreshRandomize,
                Category = "Cookie pool",
                DefaultValue = "true",
                Validator = BoolValidator("Must be true or false"),
            },
            new()
            {
                Key = ConfigKeys.BrowserUseSystem,
                Category = "Browser",
                DefaultValue = "true",
                Validator = BoolValidator("Must be true or false"),
            },
            new()
            {
                Key = ConfigKeys.BrowserStealthSelfCheck,
                Category = "Browser",
                DefaultValue = "true",
                Validator = BoolValidator("Must be true or false"),
            },
            new()
            {
                Key = ConfigKeys.BrowserHumanize,
                Category = "Browser",
                DefaultValue = "true",
                Validator = BoolValidator("Must be true or false"),
            },
            new()
            {
                Key = ConfigKeys.AutoUpgrade,
                Category = "Operations",
                DefaultValue = "true",
                Validator = BoolValidator("Must be true or false"),
            },
            new()
            {
                Key = ConfigKeys.UpgradeHours,
                Category = "Operations",
                DefaultValue = "24",
                Validator = PositiveInt("Must be a positive number of hours"),
            },
            new()
            {
                Key = ConfigKeys.AutoInstallDeps,
                Category = "Operations",
                DefaultValue = "true",
                Validator = BoolValidator("Must be true or false"),
            },
            new()
            {
                Key = ConfigKeys.LogLevel,
                Category = "Logging",
                DefaultValue = "Information",
                Validator = v => Enum.TryParse<Serilog.Events.LogEventLevel>(v, true, out _) ? null : "Must be a Serilog log level",
            },
            new()
            {
                Key = ConfigKeys.LogOutput,
                Category = "Logging",
                DefaultValue = "Console,File",
                Validator = _ => null,
            },
            new()
            {
                Key = ConfigKeys.DataDir,
                Category = "Storage",
                DefaultValue = ".flexfetch",
                Validator = v => string.IsNullOrWhiteSpace(v) ? "Must not be empty" : null,
            },
            new()
            {
                Key = ConfigKeys.ShareTokenHours,
                Category = "Security",
                DefaultValue = "0",
                Validator = NonNegativeInt("Must be a non-negative number of hours (0 means no expiry)"),
            },
            new()
            {
                Key = ConfigKeys.HttpsEnabled,
                Category = "Security",
                DefaultValue = "false",
                Validator = BoolValidator("Must be true or false"),
            },
            new()
            {
                Key = ConfigKeys.EnableCompression,
                Category = "Security",
                DefaultValue = "true",
                Validator = BoolValidator("Must be true or false"),
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
