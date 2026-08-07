namespace FlexFetch.Services.Routing;

/// <summary>
/// Matches a host against a set of domain suffixes (e.g. "cn" matches
/// "example.cn" and "cn"). Used to bypass the proxy for specific domains.
/// </summary>
public sealed class DomainSuffixMatcher
{
    private readonly string[] _suffixes;

    public DomainSuffixMatcher(IEnumerable<string> suffixes)
    {
        _suffixes = suffixes
            .Select(s => s.Trim().TrimStart('.').ToLowerInvariant())
            .Where(s => s.Length > 0)
            .Distinct()
            .ToArray();
    }

    public bool IsMatch(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var h = host.TrimEnd('.').ToLowerInvariant();
        return _suffixes.Any(s =>
            h.Equals(s, StringComparison.Ordinal)
            || h.EndsWith("." + s, StringComparison.Ordinal));
    }
}
