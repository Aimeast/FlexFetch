using System.Net;

namespace FlexFetch.Services.Routing;

/// <summary>
/// Routes everything through the configured global proxy (when set).
/// </summary>
public sealed class GlobalProxyPolicy : IRoutePolicy
{
    private readonly Func<string?> _proxyProvider;

    public GlobalProxyPolicy(Func<string?> proxyProvider)
    {
        _proxyProvider = proxyProvider;
    }

    public string Name => "GlobalProxy";

    public RouteVerdict Evaluate(Uri url, IReadOnlyList<IPAddress> resolvedIps) =>
        string.IsNullOrWhiteSpace(_proxyProvider()) ? RouteVerdict.Abstain : RouteVerdict.UseProxy;
}
