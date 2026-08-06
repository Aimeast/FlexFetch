using System.Net;

namespace FlexFetch.Services.Route;

/// <summary>
/// Bypasses the proxy when any resolved IP of the target falls inside the
/// configured CIDR list (direct connection).
/// </summary>
public sealed class BypassCidrPolicy : IRoutePolicy
{
    private readonly CidrMatcher _matcher;

    public BypassCidrPolicy(CidrMatcher matcher)
    {
        _matcher = matcher;
    }

    public string Name => "BypassCidr";

    public RouteVerdict Evaluate(Uri url, IReadOnlyList<IPAddress> resolvedIps) =>
        resolvedIps.Any(_matcher.IsMatch) ? RouteVerdict.Direct : RouteVerdict.Abstain;
}
