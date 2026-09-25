using FlexFetch.Config;
using FlexFetch.Services.Routing;

namespace FlexFetch.Services.Session;

/// <summary>
/// Proxy environment resolution for installer child processes (deno
/// install, the Playwright browser downloader): those tools cannot use a
/// socks5 proxy - a socks value in their environment hangs or fails the
/// download silently. They therefore prefer the dedicated
/// network.httpProxy (http/https) when configured, fall back to the
/// primary proxy when it is http(s) itself, and otherwise run direct
/// (mirrored registries / the Playwright CDN are directly reachable).
/// Media and site traffic is unaffected: yt-dlp, Firefox and the .NET
/// HTTP stack follow the primary network.proxy with its route rules.
/// </summary>
public static class InstallerProxyEnv
{
    /// <summary>Resolves the HTTP(S)_PROXY/HTTP_PROXY env entries for an
    /// installer child process (empty = run direct).</summary>
    public static IReadOnlyDictionary<string, string> Resolve(
        IConfiguration config, IProxyService proxy, string targetUrl)
    {
        var dedicated = config[ConfigKeys.NetworkHttpProxy.Replace('.', ':')];
        if (!string.IsNullOrWhiteSpace(dedicated))
        {
            return EnvFor(dedicated);
        }

        return EnvFor(proxy.GetProxyUri(new Uri(targetUrl)));
    }

    /// <summary>
    /// Env entries for a proxy URL: HTTP_PROXY/HTTPS_PROXY for http/https
    /// proxies, nothing for socks URLs (unsupported - they hang or fail the
    /// download silently) and nothing when unproxied.
    /// </summary>
    public static IReadOnlyDictionary<string, string> EnvFor(string? proxyUri)
    {
        if (string.IsNullOrWhiteSpace(proxyUri)
            || (!proxyUri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !proxyUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            return new Dictionary<string, string>();
        }

        return new Dictionary<string, string>
        {
            ["HTTPS_PROXY"] = proxyUri,
            ["HTTP_PROXY"] = proxyUri,
        };
    }
}
