namespace FlexFetch.Services.Session;

/// <summary>
/// Plans the Playwright browser installation executed by the
/// <c>--install-browser</c> child process. The browser itself always comes
/// from the Playwright download; the OS-level libraries it links against are
/// installed too (Linux only - apt handles them there, and the app cannot
/// ship them), so a slim image can stay without any browser stack baked in.
/// </summary>
public static class BrowserInstaller
{
    /// <summary>
    /// CLI invocations that make the browser runnable, in order: OS libraries
    /// first (Linux only; idempotent when already present), then the browser
    /// download (a no-op when the executable already exists).
    /// </summary>
    public static string[][] Steps(string browserName, bool isWindows) =>
        isWindows
            ? new[] { new[] { "install", browserName } }
            : new[] { new[] { "install-deps", browserName }, new[] { "install", browserName } };

    /// <summary>
    /// apt reads the lowercase proxy variables only, while the parent process
    /// resolved the uppercase ones for the driver itself - copy them over so
    /// <c>install-deps</c> reaches the package mirrors the same way.
    /// </summary>
    public static void MirrorProxyEnvToLowerCase()
    {
        foreach (var (upper, lower) in new[] { ("HTTP_PROXY", "http_proxy"), ("HTTPS_PROXY", "https_proxy") })
        {
            var value = Environment.GetEnvironmentVariable(upper);
            if (!string.IsNullOrWhiteSpace(value))
            {
                Environment.SetEnvironmentVariable(lower, value);
            }
        }
    }

    /// <summary>Apt's drop-in configuration file; a file here wins over every
    /// environment heuristic (apt fetches run under the sandboxed _apt user,
    /// where inherited env is the least reliable channel).</summary>
    public const string AptProxyConfPath = "/etc/apt/apt.conf.d/95flexfetch-proxy";

    /// <summary>
    /// Pins the resolved http proxy into apt's own configuration before
    /// <c>install-deps</c> runs, so the package download follows the same
    /// proxy policy even when environment inheritance fails. No-op on
    /// Windows or with a blank proxy (apt then runs direct, the documented
    /// installer fallback).
    /// </summary>
    public static void WriteAptProxyConf(string? proxyUrl, bool isWindows, string confPath = AptProxyConfPath)
    {
        if (isWindows || string.IsNullOrWhiteSpace(proxyUrl))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(confPath)!);
        File.WriteAllText(confPath,
            $"Acquire::http::Proxy \"{proxyUrl}\";\nAcquire::https::Proxy \"{proxyUrl}\";\n");
    }

    /// <summary>
    /// True when the OS-level libraries are already in place: always on
    /// Windows (no apt step there), otherwise when the marker written after a
    /// successful <c>install-deps</c> matches the current Playwright version.
    /// </summary>
    public static bool OsDepsInstalled(string markerPath, string currentVersion) =>
        OsDepsInstalled(markerPath, currentVersion, OperatingSystem.IsWindows());

    public static bool OsDepsInstalled(string markerPath, string currentVersion, bool isWindows) =>
        isWindows || (File.Exists(markerPath) && File.ReadAllText(markerPath).Trim() == currentVersion);

    /// <summary>Environment variable pointing apt at a mirror for the
    /// runtime browser-dependency install: host and path without scheme
    /// (e.g. mirrors.example.com/ubuntu). Empty keeps the official archive
    /// hosts, upgraded to https.</summary>
    public const string AptMirrorEnv = "FLEXFETCH_APT_MIRROR";

    /// <summary>
    /// Rewrites the image's apt sources before <c>install-deps</c> runs.
    /// Plain http to the Ubuntu archives is the least reliable route through
    /// restrictive proxies (502s, error pages masquerading as unsigned
    /// Release files), while the https endpoints of the same hosts serve
    /// signed content; the container filesystem is ephemeral, so the rewrite
    /// is safe to apply on every install run. When a mirror is configured
    /// (<see cref="AptMirrorEnv"/>), both archive hosts are pointed at it
    /// over https instead. Returns one notice per rewritten file.
    /// </summary>
    public static IReadOnlyList<string> PrepareAptSources(string? mirror, bool isWindows,
        string sourcesDir = "/etc/apt/sources.list.d", string legacyList = "/etc/apt/sources.list")
    {
        var notices = new List<string>();
        if (isWindows)
        {
            return notices;
        }

        var replacementHost = NormalizeMirror(mirror);
        var files = new List<string>();
        if (Directory.Exists(sourcesDir))
        {
            files.AddRange(Directory.EnumerateFiles(sourcesDir, "*.sources"));
        }
        if (File.Exists(legacyList))
        {
            files.Add(legacyList);
        }

        foreach (var file in files)
        {
            var original = File.ReadAllText(file);
            var rewritten = RewriteUbuntuUris(original, replacementHost);
            if (rewritten != original)
            {
                File.WriteAllText(file, rewritten);
                notices.Add($"{Path.GetFileName(file)}: apt sources rewritten to " +
                    (replacementHost is null ? "https (official archive)" : $"https://{replacementHost}"));
            }
        }

        return notices;
    }

    private static string? NormalizeMirror(string? mirror)
    {
        if (string.IsNullOrWhiteSpace(mirror))
        {
            return null;
        }

        var normalized = mirror.Trim();
        foreach (var scheme in new[] { "https://", "http://" })
        {
            if (normalized.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[scheme.Length..];
            }
        }

        return normalized.TrimEnd('/');
    }

    private static string RewriteUbuntuUris(string content, string? mirrorHost)
    {
        foreach (var host in new[] { "archive.ubuntu.com/ubuntu", "security.ubuntu.com/ubuntu" })
        {
            var replacement = mirrorHost is null ? $"https://{host}" : $"https://{mirrorHost}";
            content = System.Text.RegularExpressions.Regex.Replace(
                content, $"https?://{System.Text.RegularExpressions.Regex.Escape(host)}", replacement);
        }

        return content;
    }
}
