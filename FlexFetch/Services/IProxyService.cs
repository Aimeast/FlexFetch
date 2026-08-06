namespace FlexFetch.Services;

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
}
