using System.Net;
using System.Text.Json;
using FlexFetch.Config;
using Microsoft.Extensions.Primitives;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Services.Routing;

/// <summary>
/// Full proxy routing: a global proxy (HTTP/SOCKS5) plus an ordered route
/// rule list. Each rule matches by domain suffix and/or CIDR and decides
/// "use proxy" or "direct"; unmatched traffic falls back to the configured
/// default action. Downloads and the browser share this decision through
/// the IProxyService contract.
///
/// Rules are read from configuration with precedence: appsettings
/// (Network:RouteRules JSON array) first, then the runtime config store.
/// </summary>
public sealed class ProxyService : IProxyService, IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ILogger _log;
    private readonly string _dataDir;
    private readonly IDisposable _reloadRegistration;
    private volatile RouteRuleChain _chain;

    public ProxyService(IConfiguration configuration, ILogger log, string dataDir)
    {
        _configuration = configuration;
        _log = log;
        _dataDir = dataDir;
        _chain = BuildChain();
        // The editable data-dir file is loaded with reloadOnChange: rebuild
        // the compiled rules when it changes instead of requiring a restart.
        _reloadRegistration = ChangeToken.OnChange(configuration.GetReloadToken, ReloadChain);
    }

    private void ReloadChain()
    {
        _chain = BuildChain();
        _log.Information("Network route rules reloaded");
    }

    public bool ShouldProxy(Uri url)
    {
        var proxy = GetProxyAddress();
        if (string.IsNullOrWhiteSpace(proxy))
        {
            return false;
        }

        var chain = _chain;
        var ips = chain.RequiresIpResolution ? ResolveIps(url) : Array.Empty<IPAddress>();
        return chain.Evaluate(url, ips) == RouteAction.UseProxy;
    }

    public HttpMessageHandler CreateHandler(Uri url)
    {
        var handler = new SocketsHttpHandler
        {
            // All http downloads follow redirects (e.g. yt-dlp/deno latest
            // release URLs 302 to the real file; pages may redirect too).
            AllowAutoRedirect = true,
            UseCookies = false,
            // A dead proxy must not hang a request indefinitely.
            ConnectTimeout = TimeSpan.FromSeconds(15),
            // "Direct" must mean direct: the default picks up the process
            // environment's proxy (HTTP_PROXY/ALL_PROXY), so a route rule
            // saying Direct would still be tunnelled - loopback requests
            // would even be handed to a local proxy that cannot serve them.
            UseProxy = false,
        };

        var proxy = GetProxyAddress();
        if (!string.IsNullOrWhiteSpace(proxy) && ShouldProxy(url))
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }

        return handler;
    }

    public void Dispose() => _reloadRegistration.Dispose();

    /// <summary>Proxy URI for external tools (yt-dlp --proxy), or null for direct.</summary>
    public string? GetProxyUri(Uri url) =>
        ShouldProxy(url) ? GetProxyAddress() : null;

    /// <summary>
    /// Fast, DNS-free proxy decision for the browser route predicate: only
    /// domain-suffix rules and the default action are consulted. CIDR rules
    /// are skipped here (they need IP resolution); the browser handler
    /// forwards every request the predicate matches, so no DNS resolution
    /// ever runs on the Playwright routing path.
    /// </summary>
    public bool ShouldProxyFast(Uri url)
    {
        var proxy = GetProxyAddress();
        if (string.IsNullOrWhiteSpace(proxy))
        {
            return false;
        }

        return _chain.EvaluateDomainsOnly(url) == RouteAction.UseProxy;
    }

    private string? GetProxyAddress()
    {
        var raw = GetConfig(ConfigKeys.Proxy);
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    /// <summary>
    /// Reads a config value with fallback: the appsettings value for the key,
    /// then the registry default. Keys are dot-separated here, colons in
    /// IConfiguration.
    /// </summary>
    private string GetConfig(string key) => ConfigRegistry.From(_configuration, key);

    private RouteRuleChain BuildChain()
    {
        var rules = new List<IRouteRule>();
        foreach (var ruleConfig in ReadRules())
        {
            DomainSuffixMatcher? domains = null;
            if (ruleConfig.Domains is { Count: > 0 })
            {
                domains = new DomainSuffixMatcher(ruleConfig.Domains);
            }

            CidrMatcher? cidrs = null;
            if (ruleConfig.CidrFiles is { Count: > 0 })
            {
                cidrs = new CidrMatcher();
                cidrs.Load(ReadCidrLines(ruleConfig.CidrFiles));
            }

            if (domains is not null || cidrs is not null)
            {
                rules.Add(new RouteRule(ruleConfig.Action, domains, cidrs));
            }
        }

        var defaultAction = GetConfig(ConfigKeys.DefaultAction) == "Direct"
            ? RouteAction.Direct
            : RouteAction.UseProxy;

        return new RouteRuleChain(rules, defaultAction);
    }

    /// <summary>
    /// Reads the route rules with precedence:
    /// 1. appsettings "Network:RouteRules" bound structurally (JSON array);
    /// 2. the "network:routeRules" string value as a JSON string (same schema).
    /// </summary>
    private List<RouteRuleConfig> ReadRules()
    {
        var section = _configuration.GetSection("Network:RouteRules");
        if (section.Exists())
        {
            var rules = section.Get<List<RouteRuleConfig>>();
            if (rules is { Count: > 0 })
            {
                return rules;
            }
        }

        var raw = _configuration[ConfigKeys.RouteRules.Replace('.', ':')];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new List<RouteRuleConfig>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<RouteRuleConfig>>(raw) ?? new List<RouteRuleConfig>();
        }
        catch (JsonException ex)
        {
            _log.Warning("Invalid route rules JSON: {Message}", ex.Message);
            return new List<RouteRuleConfig>();
        }
    }

    private IEnumerable<string> ReadCidrLines(IEnumerable<string> cidrFiles)
    {
        foreach (var file in cidrFiles)
        {
            var path = ResolveFilePath(file);
            if (File.Exists(path))
            {
                foreach (var line in File.ReadLines(path))
                {
                    yield return line;
                }
            }
        }
    }

    /// <summary>
    /// Relative paths resolve against the runtime data directory (.flexfetch)
    /// so route IP files live with the other runtime data.
    /// </summary>
    private string ResolveFilePath(string file) =>
        Path.IsPathRooted(file) ? file : Path.Combine(_dataDir, file);

    /// <summary>
    /// Resolves the URL host to IPs for CIDR route matching. Hostnames are
    /// resolved with a bounded timeout: the lookup feeds the route decision
    /// (proxy vs direct), so a slow or poisoned local resolver must not hang
    /// the request path - on timeout the route falls back to domain rules +
    /// the default action. The actual SOCKS5 tunnel still sends the hostname
    /// to the proxy (ATYP=3) for remote resolution.
    /// </summary>
    private static IReadOnlyList<IPAddress> ResolveIps(Uri url) =>
        ResolveIpsAsync(url).GetAwaiter().GetResult();

    /// <summary>
    /// Async IP resolution with a bounded timeout, for the route decision
    /// (proxy vs direct). On timeout the route falls back to domain rules +
    /// the default action. The actual SOCKS5 tunnel still sends the hostname
    /// to the proxy (ATYP=3) for remote resolution.
    /// </summary>
    private static async Task<IReadOnlyList<IPAddress>> ResolveIpsAsync(Uri url)
    {
        // If the host is already an IP literal, use it directly.
        if (IPAddress.TryParse(url.Host, out var literal))
        {
            return new[] { literal };
        }

        try
        {
            return await Dns.GetHostAddressesAsync(url.Host)
                .WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException or TimeoutException)
        {
            return Array.Empty<IPAddress>();
        }
    }
}
