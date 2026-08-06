using FlexFetch.Config;
using FlexFetch.Data;
using FlexFetch.Services.Routing;
using System.Net;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Services.Routing;

/// <summary>
/// Full proxy routing: global proxy (HTTP/SOCKS5), CIDR bypass list and
/// composable route policies. Downloads and the browser use the same
/// egress decision through the IProxyService contract.
/// </summary>
public sealed class ProxyService : IProxyService
{
    private readonly IConfigRepository _config;
    private readonly ILogger _log;
    private readonly CidrMatcher _bypass;
    private readonly RoutePolicyChain _policies;

    public ProxyService(IConfigRepository config, ILogger log)
    {
        _config = config;
        _log = log;

        // Load the CIDR bypass list (lines from the configured file, '#' comments).
        _bypass = new CidrMatcher();
        var bypassFile = GetConfig(ConfigKeys.BypassCidrFile);
        if (!string.IsNullOrWhiteSpace(bypassFile) && File.Exists(bypassFile))
        {
            _bypass.Load(File.ReadLines(bypassFile), msg => _log.Warning("Bypass list: {Message}", msg));
        }

        // Compose route policies: default is global proxy + CIDR bypass.
        _policies = new RoutePolicyChain(BuildPolicies());
    }

    /// <summary>Reloads the bypass list and policy chain from current configuration.</summary>
    public void Reload()
    {
        var bypassFile = GetConfig(ConfigKeys.BypassCidrFile);
        var matcher = new CidrMatcher();
        if (!string.IsNullOrWhiteSpace(bypassFile) && File.Exists(bypassFile))
        {
            matcher.Load(File.ReadLines(bypassFile), msg => _log.Warning("Bypass list: {Message}", msg));
        }

        _bypass.ReplaceFrom(matcher);
        _log.Information("Proxy routing policies reloaded");
    }

    public bool ShouldProxy(Uri url)
    {
        var proxy = GetProxyAddress();
        if (string.IsNullOrWhiteSpace(proxy))
        {
            return false;
        }

        var verdict = _policies.Evaluate(url, ResolveIps(url));
        return verdict == RouteVerdict.UseProxy;
    }

    public HttpMessageHandler CreateHandler(Uri url)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
        };

        var proxy = GetProxyAddress();
        if (!string.IsNullOrWhiteSpace(proxy) && ShouldProxy(url))
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }

        return handler;
    }

    /// <summary>Proxy URI for external tools (yt-dlp --proxy), or null for direct.</summary>
    public string? GetProxyUri(Uri url) =>
        ShouldProxy(url) ? GetProxyAddress() : null;

    private string? GetProxyAddress()
    {
        var raw = GetConfig(ConfigKeys.Proxy);
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    private string GetConfig(string key) =>
        _config.Get(key) ?? ConfigRegistry.GetDefault(key);

    private IReadOnlyList<IRoutePolicy> BuildPolicies()
    {
        var policies = new List<IRoutePolicy>();
        var names = (GetConfig(ConfigKeys.RoutePolicies) ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var name in names)
        {
            switch (name)
            {
                case "GlobalProxy":
                    policies.Add(new GlobalProxyPolicy(GetProxyAddress));
                    break;
                case "BypassCidr":
                    policies.Add(new BypassCidrPolicy(_bypass));
                    break;
                default:
                    _log.Warning("Unknown route policy: {Policy}", name);
                    break;
            }
        }

        return policies;
    }

    private static IReadOnlyList<IPAddress> ResolveIps(Uri url)
    {
        // If the host is already an IP literal, use it directly.
        if (IPAddress.TryParse(url.Host, out var literal))
        {
            return new[] { literal };
        }

        try
        {
            return Dns.GetHostAddresses(url.Host);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
        {
            return Array.Empty<IPAddress>();
        }
    }
}
