using System.Reflection;

namespace FlexFetch.Services;

/// <summary>
/// Build metadata captured at compile time by the GenerateBuildInfo MSBuild
/// target (assembly-level AssemblyMetadata attributes): package version, last
/// git commit, build timestamp and configuration. A missing value (e.g. an
/// assembly built without the target) falls back to "unknown".
/// </summary>
public static class BuildInfo
{
    private static readonly IReadOnlyDictionary<string, string> Metadata = Load();

    public static string Version { get; } =
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(BuildInfo).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    public static string GitLog => Get("GitLog");

    public static string BuildDateTime => Get("BuildDateTime");

    public static string Configuration => Get("BuildConfiguration");

    private static string Get(string key) =>
        Metadata.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : "unknown";

    private static IReadOnlyDictionary<string, string> Load()
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attr in typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            dict[attr.Key] = attr.Value ?? string.Empty;
        }

        return dict;
    }
}
