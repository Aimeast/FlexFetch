namespace FlexFetch.Config;

/// <summary>
/// A single declared configuration item: key, default value, and a
/// validator. Registered once in <see cref="ConfigRegistry"/>.
/// </summary>
public sealed class ConfigItem
{
    public string Key { get; init; } = string.Empty;

    public string DefaultValue { get; init; } = string.Empty;

    /// <summary>Returns an error message, or null when the value is valid.</summary>
    public Func<string, string?> Validator { get; init; } = _ => null;
}
