using System.Text.Json;
using FlexFetch.Entities;
using FlexFetch.Services.Downloaders;
using Microsoft.AspNetCore.DataProtection;
using Serilog;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Services.Session;

/// <summary>
/// Metadata of the YouTube session file: visitor identity paired with the
/// cookie jar, plus bookkeeping for the status APIs.
/// </summary>
public sealed class YouTubeSessionMeta
{
    /// <summary>Visitor identity (from the VISITOR_INFO1_LIVE cookie) that
    /// must accompany cookie-authenticated requests as visitor_data.</summary>
    public string? VisitorData { get; set; }

    public DateTime? ImportedAt { get; set; }

    public DateTime? ExportedAt { get; set; }

    /// <summary>Health verdict of the last probe/export/canary check.</summary>
    public string Health { get; set; } = YouTubeSessionHealth.Unknown.ToString();

    public int CookieCount { get; set; }

    public string? LastProbeClass { get; set; }

    public string? LastError { get; set; }
}

/// <summary>Health verdicts surfaced by the session status APIs.</summary>
public enum YouTubeSessionHealth
{
    Unknown,

    /// <summary>The InnerTube probe accepted the jar.</summary>
    Ok,

    /// <summary>Bot challenge - the jar is being throttled.</summary>
    BotCheck,

    /// <summary>The server rotated/invalidated the jar's tickets.</summary>
    SessionRotated,

    /// <summary>Web surface rejected but InnerTube accepted (device-bound
    /// session); informational, the jar still works for yt-dlp.</summary>
    WebBindingOnly,

    /// <summary>The jar is inconsistent; only a fresh import fixes it.</summary>
    JarInconsistent,

    /// <summary>Login required - the jar no longer carries a session.</summary>
    LoginRequired,

    /// <summary>Probe or pipeline errored (network, process, timeout).</summary>
    Error,
}

/// <summary>
/// Store of the YouTube session file - the single source of truth for the
/// YouTube login session, held as one encrypted envelope
/// (session/youtube.session). The file is written ONLY by the session
/// export/import pipeline; yt-dlp, probes and the canary always receive a
/// random-named plaintext COPY (yt-dlp rewrites the cookies file it is
/// given, so the original must never be handed to it). The snapshot age is
/// taken from the meta's ExportedAt, NOT from the file mtime: meta-only
/// canary updates rewrite the file and would reset the age every check.
/// </summary>
public sealed class YouTubeSessionStore
{
    public const string SiteName = "youtube";

    private const string LegacyJarFileName = "session.txt";
    private const string LegacyMetaFileName = "session.meta.json";

    private readonly SessionFileStore _files;
    private readonly string _sessionDir;
    private readonly ILogger _log;

    public YouTubeSessionStore(SessionFileStore files, StorageService storage, ILogger log)
    {
        _files = files;
        _sessionDir = Path.Combine(storage.DataDir, "session");
        Directory.CreateDirectory(_sessionDir);
        _log = log;
        MigrateLegacySnapshot();
    }

    /// <summary>Directory receiving the one-time plaintext yt-dlp copies.</summary>
    public string CopyDirectory => Path.Combine(_sessionDir, "copies");

    /// <summary>True when a session file exists.</summary>
    public bool Exists => _files.Exists(SiteName);

    /// <summary>
    /// Age of the last full export (meta.ExportedAt), or null when missing.
    /// Deliberately not the file mtime: meta-only canary verdicts rewrite the
    /// file and would make the snapshot look freshly exported forever.
    /// </summary>
    public TimeSpan? Age
    {
        get
        {
            try
            {
                if (ReadMeta()?.ExportedAt is { } exported)
                {
                    return DateTime.UtcNow - exported;
                }
            }
            catch (SessionFileUnreadableException)
            {
                return null;
            }

            var path = _files.PathFor(SiteName);
            return File.Exists(path) ? DateTime.UtcNow - File.GetLastWriteTimeUtc(path) : null;
        }
    }

    /// <summary>Loads the session cookies (empty when missing/unreadable).</summary>
    public IReadOnlyList<CookieItem> ReadCookies()
    {
        try
        {
            return _files.Read<YouTubeSessionMeta>(SiteName)?.Cookies ?? new List<CookieItem>();
        }
        catch (SessionFileUnreadableException)
        {
            return Array.Empty<CookieItem>();
        }
    }

    /// <summary>Loads the session metadata, or null when missing/unreadable.</summary>
    public YouTubeSessionMeta? ReadMeta()
    {
        try
        {
            return _files.Read<YouTubeSessionMeta>(SiteName)?.Meta;
        }
        catch (SessionFileUnreadableException)
        {
            return null;
        }
    }

    /// <summary>
    /// Creates a random-named plaintext COPY of the session jar for exactly
    /// one yt-dlp run. The caller MUST delete the returned file when the run
    /// finishes - the copy is never merged back into the session file.
    /// </summary>
    public string? CreateSnapshotCopy()
    {
        if (!Exists)
        {
            return null;
        }

        var file = _files.Read<YouTubeSessionMeta>(SiteName);
        if (file is null)
        {
            return null;
        }

        Directory.CreateDirectory(CopyDirectory);
        var copyPath = Path.Combine(CopyDirectory, $"session-{Guid.NewGuid():N}.txt");
        File.WriteAllText(copyPath, CookieFile.BuildContent(file.Cookies));
        return copyPath;
    }

    /// <summary>
    /// Atomically replaces the session file (cookies + metadata). Callers
    /// must hold the export lock; a failed pipeline never reaches this
    /// method, so a rejected jar can never overwrite the last known good
    /// session.
    /// </summary>
    public void WriteSnapshot(IReadOnlyList<CookieItem> cookies, YouTubeSessionMeta meta)
    {
        meta.CookieCount = cookies.Count;
        _files.Write(SiteName, new SessionFile<YouTubeSessionMeta> { Cookies = cookies.ToList(), Meta = meta });
    }

    /// <summary>Updates only the metadata (e.g. canary verdicts).</summary>
    public void UpdateMeta(Action<YouTubeSessionMeta> update)
    {
        try
        {
            var file = _files.Read<YouTubeSessionMeta>(SiteName) ?? new SessionFile<YouTubeSessionMeta>();
            file.Meta ??= new YouTubeSessionMeta();
            update(file.Meta);
            _files.Write(SiteName, file);
        }
        catch (SessionFileUnreadableException)
        {
            // A meta-only update cannot repair an unreadable file; the next
            // full WriteSnapshot (a successful export/import) replaces it.
            _log.Warning("Session meta update skipped: the session file is unreadable; import the session again");
        }
    }

    /// <summary>
    /// One-time migration from the legacy plaintext pair (session.txt +
    /// session.meta.json) into the encrypted session file. Runs on every
    /// start; after a successful write the plaintext files are deleted (a
    /// kept backup would defeat the encryption). Idempotent: a leftover
    /// legacy pair next to an existing session file is only cleaned up.
    /// </summary>
    private void MigrateLegacySnapshot()
    {
        var legacyJar = Path.Combine(_sessionDir, LegacyJarFileName);
        var legacyMeta = Path.Combine(_sessionDir, LegacyMetaFileName);

        if (_files.Exists(SiteName))
        {
            DeleteIfExists(legacyJar);
            DeleteIfExists(legacyMeta);
            return;
        }

        if (!File.Exists(legacyJar))
        {
            return;
        }

        try
        {
            var cookies = CookieFile.Read(legacyJar);
            YouTubeSessionMeta? meta = null;
            if (File.Exists(legacyMeta))
            {
                try
                {
                    meta = JsonSerializer.Deserialize<YouTubeSessionMeta>(File.ReadAllText(legacyMeta),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (JsonException ex)
                {
                    _log.Warning("Legacy session metadata is malformed ({Reason}); migrating with default metadata", ex.Message);
                }
            }

            _files.Write(SiteName, new SessionFile<YouTubeSessionMeta>
            {
                Cookies = cookies.ToList(),
                Meta = meta ?? new YouTubeSessionMeta(),
            });
            DeleteIfExists(legacyJar);
            DeleteIfExists(legacyMeta);
            _log.Information("Migrated the legacy plaintext session snapshot ({Count} cookies) into the encrypted session file",
                cookies.Count);
        }
        catch (Exception ex)
        {
            // The legacy pair stays on disk; the next start retries. The
            // session reads as absent until the migration succeeds.
            _log.Error(ex, "Migrating the legacy session snapshot failed; it is retried on the next start");
        }
    }

    private void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _log.Warning("Could not delete the legacy session file {Path}: {Message}", path, ex.Message);
        }
    }

    /// <summary>
    /// Keeps only cookies whose domain belongs to YouTube (the session's
    /// minimal form). Domain equality ignores a leading dot and case; any
    /// subdomain of youtube.com counts. Merging back extra domains would
    /// grow the session on every browser round-trip, so they are dropped.
    /// </summary>
    public static IReadOnlyList<CookieItem> FilterYouTubeDomain(IReadOnlyList<CookieItem> cookies) =>
        cookies.Where(c => IsYouTubeDomain(c.Domain)).ToList();

    /// <summary>True when the (leading-dot-insensitive) domain is youtube.com or a subdomain.</summary>
    public static bool IsYouTubeDomain(string? domain)
    {
        var d = (domain ?? string.Empty).TrimStart('.').ToLowerInvariant();
        return d.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
            || d.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Merges the incoming cookies over the existing ones. The key is the
    /// (name, domain) pair - the same name on different domains is a
    /// DIFFERENT cookie and is never merged across domains; the incoming
    /// value wins within its own key. Expired cookies are dropped.
    /// </summary>
    public static IReadOnlyList<CookieItem> Merge(IReadOnlyList<CookieItem> existing, IReadOnlyList<CookieItem> incoming)
    {
        var map = new Dictionary<(string Name, string Domain), CookieItem>();
        foreach (var c in existing)
        {
            if (!IsExpired(c))
            {
                map[Key(c)] = c;
            }
        }

        foreach (var c in incoming)
        {
            if (IsExpired(c))
            {
                map.Remove(Key(c));
                continue;
            }

            map[Key(c)] = c;
        }

        return map.Values.ToList();
    }

    private static (string, string) Key(CookieItem c) =>
        (c.Name, (c.Domain ?? string.Empty).TrimStart('.').ToLowerInvariant());

    private static bool IsExpired(CookieItem c) =>
        c.ExpiresAt is not null && c.ExpiresAt.Value.ToUniversalTime() <= DateTime.UtcNow;
}
