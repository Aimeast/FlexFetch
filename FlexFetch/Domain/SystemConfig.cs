namespace FlexFetch.Domain;

/// <summary>
/// A structured configuration key-value entry (concurrency, proxy,
/// registration policy, cleanup thresholds, upgrade periods, ...).
/// </summary>
public sealed class SystemConfig
{
    public string Id { get; set; } = RandomId.New();

    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
