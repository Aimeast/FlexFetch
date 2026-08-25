using System.Net;

namespace FlexFetch.Services.Routing;

/// <summary>
/// Evaluates an ordered list of route rules; the first matching rule decides
/// the action. When no rule matches, the configured default action applies
/// (all unmatched traffic goes through the proxy or directly).
/// </summary>
public sealed class RouteRuleChain
{
    private readonly IReadOnlyList<IRouteRule> _rules;
    private readonly RouteAction _defaultAction;

    public RouteRuleChain(IEnumerable<IRouteRule> rules, RouteAction defaultAction)
    {
        _rules = rules.ToList();
        _defaultAction = defaultAction;
    }

    public RouteAction Evaluate(Uri url, IReadOnlyList<IPAddress> resolvedIps)
    {
        foreach (var rule in _rules)
        {
            if (rule.IsMatch(url, resolvedIps))
            {
                return rule.Action;
            }
        }

        return _defaultAction;
    }

    /// <summary>
    /// Evaluates the chain using domain-suffix rules only (no DNS/IP
    /// resolution). Rules that match solely by CIDR are skipped, so the
    /// result may be "stricter" than Evaluate: it is meant for fast, blocking-
    /// free decisions on paths that must not wait on DNS (e.g. the browser
    /// route predicate).
    /// </summary>
    public RouteAction EvaluateDomainsOnly(Uri url)
    {
        foreach (var rule in _rules)
        {
            if (rule.IsDomainMatch(url.Host))
            {
                return rule.Action;
            }
        }

        return _defaultAction;
    }
}
