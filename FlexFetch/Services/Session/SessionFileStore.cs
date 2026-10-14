using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlexFetch.Entities;
using FlexFetch.Services;
using Microsoft.AspNetCore.DataProtection;
using Serilog;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Services.Session;

/// <summary>
/// The plaintext payload of one site session file: the cookie jar paired
/// with the site's own metadata. The envelope is the only thing that gets
/// encrypted; Site and Version travel inside it so a payload can never be
/// replayed for another site or silently parsed by a future format.
/// </summary>
public sealed class SessionFile<TMeta>
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public string Site { get; set; } = string.Empty;

    public List<CookieItem> Cookies { get; set; } = new();

    public TMeta? Meta { get; set; }
}

/// <summary>
/// The session file exists but cannot be decrypted or parsed (tampered,
/// truncated, key ring lost, unsupported version, wrong site). The file is
/// never deleted by the app - a fresh import overwrites it.
/// </summary>
public sealed class SessionFileUnreadableException : Exception
{
    public SessionFileUnreadableException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
/// Shared storage for the per-site session files
/// ({dataDir}/session/{site}.session). Each file is one encrypted envelope:
/// JSON (cookies + site meta) through the Data Protection key ring
/// ({dataDir}/keys), written atomically. Site isolation is enforced twice -
/// by the per-site IDataProtector purpose AND by the Site field inside the
/// envelope.
/// </summary>
public sealed class SessionFileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _sessionDir;
    private readonly IDataProtectionProvider _protection;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, IDataProtector> _protectors = new(StringComparer.Ordinal);

    public SessionFileStore(StorageService storage, IDataProtectionProvider protection, ILogger log)
    {
        _sessionDir = Path.Combine(storage.DataDir, "session");
        Directory.CreateDirectory(_sessionDir);
        _protection = protection;
        _log = log;
    }

    public string PathFor(string site) => Path.Combine(_sessionDir, site.ToLowerInvariant() + ".session");

    /// <summary>True when the site's session file exists.</summary>
    public bool Exists(string site) => File.Exists(PathFor(site));

    /// <summary>
    /// Reads and decrypts the site's session file, or null when absent.
    /// Throws SessionFileUnreadableException when the file exists but cannot
    /// be turned back into an envelope.
    /// </summary>
    public SessionFile<TMeta>? Read<TMeta>(string site)
    {
        var path = PathFor(site);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var plain = Protector(site).Unprotect(File.ReadAllBytes(path));
            var file = JsonSerializer.Deserialize<SessionFile<TMeta>>(plain, JsonOptions);
            if (file is null)
            {
                throw new SessionFileUnreadableException($"Session file for {site} holds an empty envelope");
            }

            if (file.Version != SessionFile<TMeta>.CurrentVersion)
            {
                throw new SessionFileUnreadableException(
                    $"Session file for {site} has unsupported version {file.Version} "
                    + $"(expected {SessionFile<TMeta>.CurrentVersion}); import the session again");
            }

            if (!string.Equals(file.Site, site, StringComparison.OrdinalIgnoreCase))
            {
                throw new SessionFileUnreadableException(
                    $"Session file for {site} carries site '{file.Site}'; import the session again");
            }

            return file;
        }
        catch (SessionFileUnreadableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Session file for {Site} is unreadable ({Reason}); treat it as absent, importing again fixes it",
                site, ex.GetType().Name);
            throw new SessionFileUnreadableException($"Session file for {site} is unreadable: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Encrypts and atomically replaces the site's session file. A crash
    /// mid-write leaves only a .tmp leftover, never a broken file.
    /// </summary>
    public void Write<TMeta>(string site, SessionFile<TMeta> file)
    {
        file.Version = SessionFile<TMeta>.CurrentVersion;
        file.Site = site;
        var blob = Protector(site).Protect(JsonSerializer.SerializeToUtf8Bytes(file, JsonOptions));
        Directory.CreateDirectory(_sessionDir);
        var path = PathFor(site);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, blob);
        RestrictPermissions(tmp);
        File.Move(tmp, path, overwrite: true);
    }

    private IDataProtector Protector(string site) =>
        _protectors.GetOrAdd(site.ToLowerInvariant(), s => _protection.CreateProtector("FlexFetch.SessionFiles." + s));

    private static void RestrictPermissions(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
