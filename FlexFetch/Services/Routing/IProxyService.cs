namespace FlexFetch.Services.Routing;

/// <summary>
/// Proxy routing contract shared by downloaders and the browser service.
/// The concrete routing logic (global proxy, CIDR bypass, policy composition)
/// is implemented by ProxyService; consumers only depend on this interface.
/// </summary>
public interface IProxyService
{
    /// <summary>Whether the given URL should go through the proxy.</summary>
    bool ShouldProxy(Uri url);

    /// <summary>Builds an HttpMessageHandler configured with the routing for the URL (proxy or direct).</summary>
    HttpMessageHandler CreateHandler(Uri url);

    /// <summary>Proxy URI string for external tools (yt-dlp --proxy), or null for direct.</summary>
    string? GetProxyUri(Uri url);

    /// <summary>Global proxy address for the browser context, or null for direct.</summary>
    string? GetBrowserProxyAddress();

    /// <summary>
    /// Fast proxy decision without DNS: domain-suffix rules plus the default
    /// action only. Safe to call on the Playwright routing path, where a
    /// blocking DNS lookup would stall navigation with net::ERR_FAILED.
    /// </summary>
    bool ShouldProxyFast(Uri url);
}
