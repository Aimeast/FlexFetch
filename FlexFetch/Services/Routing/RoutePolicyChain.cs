using System.Net;

namespace FlexFetch.Services.Routing;

/// <summary>
/// Evaluates an ordered list of route policies. Direct always wins over
/// UseProxy (bypass rules override the global proxy); if nothing decides,
/// Abstain is returned and the caller applies its default.
/// </summary>
public sealed class RoutePolicyChain
{
    private readonly IReadOnlyList<IRoutePolicy> _policies;

    public RoutePolicyChain(IEnumerable<IRoutePolicy> policies)
    {
        _policies = policies.ToList();
    }

    public RouteVerdict Evaluate(Uri url, IReadOnlyList<IPAddress> resolvedIps)
    {
        var useProxy = false;
        foreach (var policy in _policies)
        {
            var verdict = policy.Evaluate(url, resolvedIps);
            if (verdict == RouteVerdict.Direct)
            {
                return RouteVerdict.Direct;
            }
            if (verdict == RouteVerdict.UseProxy)
            {
                useProxy = true;
            }
        }

        return useProxy ? RouteVerdict.UseProxy : RouteVerdict.Abstain;
    }
}
