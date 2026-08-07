using System.Net;
using System.Text.Json.Serialization;

namespace FlexFetch.Services.Routing;

/// <summary>
/// Configuration model of a single route rule: a domain-suffix matcher and/or
/// CIDR files, plus the action applied when the rule matches. Domains and
/// CIDR matches are OR-ed: the rule matches when either side matches.
/// </summary>
public sealed class RouteRuleConfig
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RouteAction Action { get; set; } = RouteAction.Direct;

    /// <summary>Domain suffixes (e.g. "cn"); empty means no domain matching.</summary>
    public List<string>? Domains { get; set; }

    /// <summary>CIDR list file paths (relative to the runtime data dir).</summary>
    public List<string>? CidrFiles { get; set; }
}

/// <summary>A compiled route rule.</summary>
public sealed class RouteRule : IRouteRule
{
    private readonly DomainSuffixMatcher? _domains;
    private readonly CidrMatcher? _cidrs;

    public RouteRule(RouteAction action, DomainSuffixMatcher? domains, CidrMatcher? cidrs)
    {
        Action = action;
        _domains = domains;
        _cidrs = cidrs;
    }

    public RouteAction Action { get; }

    public bool IsMatch(Uri url, IReadOnlyList<IPAddress> resolvedIps) =>
        (_domains is not null && _domains.IsMatch(url.Host))
        || (_cidrs is not null && resolvedIps.Any(_cidrs.IsMatch));
}
