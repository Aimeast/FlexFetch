using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

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

    public static WebApplicationFactory<Program> CreateFactory(string dataDir, string adminPassword = "admin-pass-1")
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Data:Dir", dataDir);
            builder.UseSetting("Admin:InitialPassword", adminPassword);
            // Disable file logging so the temporary data dir is not locked
            // by a rolling log file during cleanup.
            builder.UseSetting("Logging:WriteToFile", "false");
        });
    }
}

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
