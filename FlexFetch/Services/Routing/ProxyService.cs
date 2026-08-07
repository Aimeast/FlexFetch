using FlexFetch.Config;
using FlexFetch.Data;
using System.Net;
using System.Text.Json;
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
public sealed class ProxyService : IProxyService
{
    private readonly IConfigRepository _config;
    private readonly ILogger _log;
    private readonly string _dataDir;
    private readonly IConfiguration? _configuration;
    private RouteRuleChain _chain;

    public ProxyService(IConfigRepository config, ILogger log, string dataDir, IConfiguration? configuration = null)
    {
        _config = config;
        _log = log;
        _dataDir = dataDir;
        _configuration = configuration;
        _chain = BuildChain();
    }

    /// <summary>Rebuilds the route chain from current configuration.</summary>
    public void Reload()
    {
        _chain = BuildChain();
        _log.Information("Proxy routing rules reloaded");
    }

    public bool ShouldProxy(Uri url)
    {
        var proxy = GetProxyAddress();
        if (string.IsNullOrWhiteSpace(proxy))
        {
            return false;
        }

        return _chain.Evaluate(url, ResolveIps(url)) == RouteAction.UseProxy;
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

    /// <summary>
    /// Reads a config value with precedence: appsettings (Network:*) first,
    /// then the runtime config store, then the registry default.
    /// </summary>
    private string GetConfig(string key)
    {
        if (_configuration is not null)
        {
            // ConfigKeys are "network.proxy" etc.; IConfiguration keys are
            // case-insensitive, so "Network:proxy" matches "Network:Proxy".
            var section = "Network:" + key.Split('.').Last();
            var value = _configuration[section];
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return _config.Get(key) ?? ConfigRegistry.GetDefault(key);
    }

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
    /// 2. the runtime config store value as a JSON string (same schema).
    /// </summary>
    private List<RouteRuleConfig> ReadRules()
    {
        if (_configuration is not null)
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
        }

        var raw = _config.Get(ConfigKeys.RouteRules);
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
