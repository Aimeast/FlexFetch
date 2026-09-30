using FlexFetch.Services.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using Serilog;

namespace FlexFetch.Tests;

/// <summary>
/// Creates the application against an isolated temporary data directory
/// so tests never touch the repository's real data folder.
/// </summary>
public static class TestApp
{
    public static string CreateTempDataDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flexfetch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static WebApplicationFactory<Program> CreateFactory(string dataDir, bool allowAnonymous = false)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("storage:dataDir", dataDir);
            builder.UseSetting("Admin:InitialPassword", "admin-pass-1");
            // Disable file logging so the temporary data dir is not locked
            // by a rolling log file during cleanup.
            builder.UseSetting("Logging:WriteToFile", "false");
            // Always pin the flag explicitly: the test host runs with the
            // Development environment and would otherwise inherit whatever
            // appsettings.Development.json happens to set for it.
            builder.UseSetting("account:allowAnonymous", allowAnonymous ? "true" : "false");
        });
    }
}

/// <summary>
/// An IProxyService fake with no proxy configured: every URL goes direct.
/// </summary>
public sealed class DirectProxyService : IProxyService
{
    public bool ShouldProxy(Uri url) => false;

    public bool ShouldProxyFast(Uri url) => false;

    public HttpMessageHandler CreateHandler(Uri url) => new SocketsHttpHandler { UseProxy = false };

    public string? GetProxyUri(Uri url) => null;
}

/// <summary>
/// Serilog logger that swallows records: tests assert outcomes, not log lines.
/// </summary>
public static class TestLog
{
    public static readonly ILogger Instance = new LoggerConfiguration()
        .MinimumLevel.Warning()
        .CreateLogger();
}

/// <summary>Shape of the /api/auth/login response used by API tests.</summary>
internal sealed record LoginResponse(string Id, string UserName, string Role);

/// <summary>Shape of the public /api/auth/status response used by API tests.</summary>
internal sealed record StatusResponse(bool Authenticated, string? UserName, string? Role, bool AnonymousEnabled);

/// <summary>
/// Mutable in-memory IConfiguration for unit tests: values are set with the
/// dot-separated ConfigKeys key form and read back in IConfiguration's colon
/// form, mirroring ConfigRegistry.From. GetSection always reports an empty,
/// non-existent section so structured route rules are not exercised here.
/// </summary>
public sealed class TestConfig : IConfiguration
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string key, string value) => _values[key.Replace('.', ':')] = value;

    public string? this[string key]
    {
        get => _values.TryGetValue(key, out var value) ? value : null;
        set => _values[key] = value!;
    }

    public IConfigurationSection GetSection(string key) => new EmptySection();

    public IEnumerable<IConfigurationSection> GetChildren() => Array.Empty<IConfigurationSection>();

    public IChangeToken GetReloadToken() => new NoopChangeToken();

    private sealed class EmptySection : IConfigurationSection
    {
        public string Key => string.Empty;

        public string Path => string.Empty;

        public string? Value { get => null; set { } }

        public string? this[string key]
        {
            get => null;
            set { }
        }

        public IEnumerable<IConfigurationSection> GetChildren() => Array.Empty<IConfigurationSection>();

        public IConfigurationSection GetSection(string key) => this;

        public IChangeToken GetReloadToken() => new NoopChangeToken();
    }

    private sealed class NoopChangeToken : IChangeToken
    {
        public bool HasChanged => false;

        public bool ActiveChangeCallbacks => false;

        public IDisposable RegisterChangeCallback(Action<object?> callback, object? state) => new NoopDisposable();

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
