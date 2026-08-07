using System.Net;

namespace FlexFetch.Services.Routing;

/// <summary>Action applied when a route rule matches.</summary>
public enum RouteAction
{
    /// <summary>Route the request through the proxy.</summary>
    UseProxy = 0,

    /// <summary>Connect directly, bypassing the proxy.</summary>
    Direct = 1,
}

/// <summary>
/// A single route rule: a domain-suffix and/or CIDR matcher plus the action
/// to apply when the rule matches. Rules are evaluated in declaration order;
/// the first match wins. New rule types implement this interface.
/// </summary>
public interface IRouteRule
{
    /// <summary>Action to apply when this rule matches.</summary>
    RouteAction Action { get; }

    /// <summary>Returns true when the rule matches the request.</summary>
    bool IsMatch(Uri url, IReadOnlyList<IPAddress> resolvedIps);
}
