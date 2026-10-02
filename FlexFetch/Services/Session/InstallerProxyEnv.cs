using FlexFetch.Services.Routing;

namespace FlexFetch.Services.Session;

/// <summary>
/// Proxy environment resolution for installer child processes (deno
/// install, the Playwright browser downloader): those tools cannot use a
/// socks5 proxy - a socks value in their environment hangs or fails the
/// download silently. They follow the single network.proxy address: an
/// http(s) proxy is passed via HTTP(S)_PROXY env, a socks primary is
/// dropped and the install runs direct (mirrored registries / the
/// Playwright CDN are directly reachable). Media and site traffic is
/// unaffected: yt-dlp, Firefox and the .NET HTTP stack use the same
/// network.proxy with its route rules, socks included.
/// </summary>
public static class InstallerProxyEnv
{
    /// <summary>Resolves the HTTP(S)_PROXY/HTTP_PROXY env entries for an
    /// installer child process from the primary proxy policy (empty = run
    /// direct).</summary>
    public static IReadOnlyDictionary<string, string> Resolve(
        IProxyService proxy, string targetUrl)
    {
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

    /// <summary>Human-readable summary of a resolved env set for logging:
    /// the http(s) proxy address the installer will use, or "direct" when
    /// it runs without one.</summary>
    public static string Describe(IReadOnlyDictionary<string, string> env) =>
        env.TryGetValue("HTTPS_PROXY", out var proxy) && !string.IsNullOrWhiteSpace(proxy)
            ? proxy
            : "direct";
}
