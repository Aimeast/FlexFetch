using FlexFetch.Config;
using FlexFetch.Data;
using System.Net;

namespace FlexFetch.Services;

/// <summary>
/// Simple proxy service: reads the global proxy address from configuration
/// and applies it to all URLs (no bypass list yet). Replaced by the full
/// ProxyService with CIDR bypass and policy composition.
/// </summary>
public sealed class ConfigProxyService : IProxyService
{
    private readonly IConfigRepository _config;

    public ConfigProxyService(IConfigRepository config)
    {
        _config = config;
    }

    private string? ProxyAddress
    {
        get
        {
            var raw = _config.Get(ConfigKeys.Proxy) ?? ConfigRegistry.GetDefault(ConfigKeys.Proxy);
            return string.IsNullOrWhiteSpace(raw) ? null : raw;
        }
    }

    public bool ShouldProxy(Uri url) => ProxyAddress is not null;

    public HttpMessageHandler CreateHandler(Uri url)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
        };

        var proxy = ProxyAddress;
        if (proxy is not null)
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }

        return handler;
    }

    public string? GetProxyUri(Uri url) => ProxyAddress;
}
