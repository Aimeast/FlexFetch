using System.Net;

namespace FlexFetch.Services.Route;

/// <summary>Verdict of a route policy for a given request.</summary>
public enum RouteVerdict
{
    /// <summary>The policy does not decide; let the next policy decide.</summary>
    Abstain = 0,

    /// <summary>Route through the proxy.</summary>
    UseProxy = 1,

    /// <summary>Connect directly, bypassing the proxy.</summary>
    Direct = 2,
}

/// <summary>
/// A composable routing policy. Policies are evaluated in order; Direct
/// always wins over UseProxy so bypass rules take precedence over the
/// global proxy default.
/// </summary>
public interface IRoutePolicy
{
    string Name { get; }

    RouteVerdict Evaluate(Uri url, IReadOnlyList<IPAddress> resolvedIps);
}
