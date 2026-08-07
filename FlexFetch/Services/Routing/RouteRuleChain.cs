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
}
