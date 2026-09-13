using System.Diagnostics;
using FlexFetch.Entities;
using FlexFetch.Enums;
using FlexFetch.Services.Routing;
using Microsoft.Playwright;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Services.Session;

/// <summary>A page opened in a throw-away Firefox instance; disposal closes the whole browser.</summary>
public sealed class EphemeralBrowserPage : IAsyncDisposable
{
    private readonly IBrowserContext _context;
    private readonly IBrowser _browser;

    public EphemeralBrowserPage(IPage page, IBrowserContext context, IBrowser browser)
    {
        Page = page;
        _context = context;
        _browser = browser;
    }

    public IPage Page { get; }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _context.DisposeAsync();
            await _browser.DisposeAsync();
        }
        catch
        {
            // A crashed browser must not break the caller's cleanup path.
        }
    }
}

/// <summary>
/// Headless Firefox service - the session source. Holds the persistent
/// profile that the export pipeline re-seeds from the snapshot and reads the
/// cookie jar back from (no site page is loaded for jar operations; the jar
/// is only a temporary workspace between snapshots). Also opens throw-away
/// browsers for generic media sniffing, so those never touch the session
/// profile. Every cross-process wait is bounded: a hung browser must never
/// hold the export lock forever.
/// </summary>
public sealed class FirefoxBrowserService : IAsyncDisposable
{
    /// <summary>Bounded-wait baseline: launch, jar read, page read, closes.</summary>
    public static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan JarReadTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan PageReadTimeout = TimeSpan.FromSeconds(12);
    public static readonly TimeSpan PageCloseTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ContextCloseTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Domains the session browser reads cookies for (identity
    /// cookies live on the root domain, so both are queried).</summary>
    public static readonly string[] SessionJarUrls =
    {
        "https://www.youtube.com/",
        "https://m.youtube.com/",
    };

    private readonly IProxyService _proxy;
    private readonly StorageService _storage;
    private readonly IConfiguration _config;
    private readonly ILogger _log;
    private readonly string _profileDir;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private IPlaywright? _playwright;
    private IBrowserContext? _sessionContext;

    public FirefoxBrowserService(IProxyService proxy, StorageService storage, IConfiguration config, ILogger log)
    {
        _proxy = proxy;
        _storage = storage;
        _config = config;
        _log = log;
        _profileDir = Path.Combine(storage.DataDir, "profiles", "Firefox");
    }

    public string ProfileDir => _profileDir;

    /// <summary>True while the persistent session context is running.</summary>
    public bool IsRunning => _sessionContext is not null;

    /// <summary>Locates the Playwright-installed Firefox executable, or null.</summary>
    public static string? FindFirefoxExecutable()
    {
        foreach (var root in GetPlaywrightBrowserRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var dir in Directory.EnumerateDirectories(root, "firefox-*"))
            {
                var exe = OperatingSystem.IsWindows() ? Path.Combine(dir, "firefox", "firefox.exe")
                    : OperatingSystem.IsMacOS() ? Path.Combine(dir, "Firefox.app", "Contents", "MacOS", "firefox")
                    : Path.Combine(dir, "firefox", "firefox");
                if (File.Exists(exe))
                {
                    return exe;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> GetPlaywrightBrowserRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ms-playwright");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches", "ms-playwright");
        }
        else
        {
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "ms-playwright");
        }
    }

    /// <summary>True when a Playwright Firefox build is already installed.</summary>
    public bool IsFirefoxInstalled() => FindFirefoxExecutable() is not null;

    private readonly SemaphoreSlim _installLock = new(1, 1);

    /// <summary>
    /// Installs the Playwright Firefox build in a child process (the install
    /// downloads a large archive; the parent process must not block on it).
    /// Serialized: startup tasks and a lazy session launch can both want the
    /// install, and concurrent downloads corrupt the browser cache.
    /// </summary>
    public async Task EnsureInstalledAsync(CancellationToken cancellationToken = default)
    {
        if (IsFirefoxInstalled())
        {
            return;
        }

        await _installLock.WaitAsync(cancellationToken);
        try
        {
            // Re-check under the lock: another flow may have finished the
            // install while we waited.
            if (IsFirefoxInstalled())
            {
                return;
            }

            await InstallFirefoxCoreAsync(cancellationToken);
        }
        finally
        {
            _installLock.Release();
        }
    }

    private async Task InstallFirefoxCoreAsync(CancellationToken cancellationToken)
    {
        var processPath = Environment.ProcessPath ?? "dotnet";
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (Path.GetFileName(processPath).StartsWith("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // Running via `dotnet <dll>`: ProcessPath is the dotnet host, so
            // the dll path must precede the flag (a bare
            // "dotnet --install-browser" is an invalid CLI call).
            startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "FlexFetch.dll"));
            startInfo.ArgumentList.Add("--install-browser");
            startInfo.ArgumentList.Add("firefox");
        }
        else
        {
            // Published app host: the executable re-enters with the flag.
            startInfo.ArgumentList.Add("--install-browser");
            startInfo.ArgumentList.Add("firefox");
        }

        // The installer is a Node program: it supports http(s) proxy URLs
        // only, a socks5 URL makes the download fail silently. The Playwright
        // CDN is directly reachable, so with a socks-only outlet we run
        // without proxy env instead.
        // The installer is a Node program: it supports http(s) proxy URLs
        // only, a socks5 URL makes the download fail silently. network.httpProxy
        // (or an http(s) primary proxy) routes the download through the fixed
        // egress; the Playwright CDN is directly reachable as the fallback.
        foreach (var (key, value) in InstallerProxyEnv.Resolve(
            _config, _proxy, "https://cdn.playwright.dev/"))
        {
            startInfo.Environment[key] = value;
        }

        _log.Information("Installing Playwright Firefox");
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Failed to start the Firefox installer process");
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        var finished = await Task.WhenAny(
            process.WaitForExitAsync(cancellationToken),
            Task.Delay(TimeSpan.FromMinutes(30), cancellationToken));
        if (finished != process.WaitForExitAsync(CancellationToken.None) && !process.HasExited)
        {
            _log.Warning("Firefox install timed out; killing installer process");
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw new TimeoutException("Firefox install did not finish in time");
        }

        var output = LastLines((await stdout).Trim(), 3)
            + Environment.NewLine + LastLines((await stderr).Trim(), 3);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Firefox install exited with code {process.ExitCode}: {output.Trim()}");
        }

        _log.Information("Playwright Firefox install finished: {Output}", output.Trim());
    }

    /// <summary>Last up-to-N non-empty lines of a text (a short output must not break slicing).</summary>
    private static string LastLines(string text, int count)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" | ", lines[^Math.Min(count, lines.Length)..]);
    }

    /// <summary>
    /// Writes the profile's user.js proxy preferences before launch. Firefox
    /// reads user.js on every start, so the policy survives persistent-profile
    /// launches. SOCKS5 is configured with remote DNS (the Firefox equivalent
    /// of socks5h); when the policy says direct, proxying is switched off.
    /// </summary>
    public void WriteProxyPreferences()
    {
        Directory.CreateDirectory(_profileDir);
        var proxyUri = _proxy.GetProxyUri(new Uri("https://www.youtube.com/"));
        var prefs = new System.Text.StringBuilder();
        if (proxyUri is null)
        {
            prefs.AppendLine("user_pref(\"network.proxy.type\", 0);");
        }
        else if (Uri.TryCreate(proxyUri, UriKind.Absolute, out var uri))
        {
            prefs.AppendLine("user_pref(\"network.proxy.type\", 1);");
            if (string.Equals(uri.Scheme, "socks5", StringComparison.OrdinalIgnoreCase))
            {
                prefs.AppendLine($"user_pref(\"network.proxy.socks\", \"{uri.Host}\");");
                prefs.AppendLine($"user_pref(\"network.proxy.socks_port\", {uri.Port});");
                prefs.AppendLine("user_pref(\"network.proxy.socks_version\", 5);");
                prefs.AppendLine("user_pref(\"network.proxy.socks_remote_dns\", true);");
            }
            else
            {
                prefs.AppendLine($"user_pref(\"network.proxy.http\", \"{uri.Host}\");");
                prefs.AppendLine($"user_pref(\"network.proxy.http_port\", {uri.Port});");
                prefs.AppendLine($"user_pref(\"network.proxy.ssl\", \"{uri.Host}\");");
                prefs.AppendLine($"user_pref(\"network.proxy.ssl_port\", {uri.Port});");
            }
        }

        File.WriteAllText(Path.Combine(_profileDir, "user.js"), prefs.ToString());
    }

    /// <summary>Launches the persistent session context (serialized, bounded).</summary>
    public async Task EnsureSessionAsync()
    {
        if (_sessionContext is not null)
        {
            return;
        }

        await _lock.WaitAsync();
        try
        {
            if (_sessionContext is not null)
            {
                return;
            }

            if (!IsFirefoxInstalled())
            {
                await EnsureInstalledAsync();
            }

            _playwright ??= await Playwright.CreateAsync();
            WriteProxyPreferences();

            var options = new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = true,
                IgnoreHTTPSErrors = true,
                Timeout = (float)LaunchTimeout.TotalMilliseconds,
                ViewportSize = new ViewportSize { Width = 1366, Height = 768 },
            };
            Directory.CreateDirectory(_profileDir);
            _sessionContext = await _playwright.Firefox.LaunchPersistentContextAsync(_profileDir, options);
            _log.Information("Firefox session browser started ({Profile})", _profileDir);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Seeds the cookie jar from a snapshot. Cookies named with the __Host-
    /// prefix are injected by URL only (no Domain/Path attributes): browsers
    /// silently reject a __Host- cookie that carries explicit attributes.
    /// </summary>
    public async Task SeedCookiesAsync(IReadOnlyList<CookieItem> cookies)
    {
        var context = _sessionContext ?? throw new InvalidOperationException("Session browser is not running");
        var args = cookies.Select(ToPlaywrightCookie).ToArray();
        if (args.Length == 0)
        {
            return;
        }

        await context.AddCookiesAsync(args).WaitAsync(JarReadTimeout);
    }

    /// <summary>
    /// Silently reads the cookie jar for the session domains - no site page
    /// is ever loaded for this (a lossy browser round-trip must stay off the
    /// critical path).
    /// </summary>
    public async Task<IReadOnlyList<CookieItem>> ReadJarAsync()
    {
        var context = _sessionContext ?? throw new InvalidOperationException("Session browser is not running");
        var cookies = await context.CookiesAsync(SessionJarUrls).WaitAsync(JarReadTimeout);
        return cookies.Select(ToCookieItem).ToList();
    }

    /// <summary>Opens a page on the session context (humanized visits).</summary>
    public async Task<IPage> NewSessionPageAsync()
    {
        var context = _sessionContext ?? throw new InvalidOperationException("Session browser is not running");
        return await context.NewPageAsync();
    }

    /// <summary>
    /// Opens a page in a throw-away Firefox (fresh non-persistent browser):
    /// generic media sniffing must never touch the session profile. The
    /// caller disposes the returned handle; the whole browser process exits
    /// with it.
    /// </summary>
    public async Task<EphemeralBrowserPage> OpenEphemeralPageAsync(string targetUrl)
    {
        _playwright ??= await Playwright.CreateAsync();
        var launchOptions = new BrowserTypeLaunchOptions
        {
            Headless = true,
            Timeout = (float)LaunchTimeout.TotalMilliseconds,
        };
        var uri = new Uri(targetUrl);
        var proxyUri = _proxy.GetProxyUri(uri);
        if (!string.IsNullOrWhiteSpace(proxyUri) && _proxy.ShouldProxyFast(uri))
        {
            launchOptions.Proxy = new Proxy { Server = proxyUri };
        }

        var browser = await _playwright.Firefox.LaunchAsync(launchOptions).WaitAsync(LaunchTimeout);
        try
        {
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1366, Height = 768 },
            }).WaitAsync(ContextCloseTimeout);
            var page = await context.NewPageAsync().WaitAsync(PageReadTimeout);
            return new EphemeralBrowserPage(page, context, browser);
        }
        catch
        {
            await browser.DisposeAsync();
            throw;
        }
    }

    /// <summary>Closes the session context (bounded) and releases the profile lock.</summary>
    public async Task CloseSessionAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_sessionContext is not null)
            {
                await _sessionContext.CloseAsync().WaitAsync(ContextCloseTimeout);
                _sessionContext = null;
            }

            _playwright?.Dispose();
            _playwright = null;
        }
        catch (TimeoutException)
        {
            // The close timed out: drop the references anyway so the next
            // export starts fresh; a hung browser process dies with the app.
            _sessionContext = null;
            _playwright?.Dispose();
            _playwright = null;
            _log.Warning("Firefox session context close timed out; state discarded");
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync() => await CloseSessionAsync();

    internal static Cookie ToPlaywrightCookie(CookieItem c)
    {
        var host = (c.Domain ?? string.Empty).TrimStart('.');
        var cookie = new Cookie
        {
            Name = c.Name,
            Value = c.Value,
            Path = string.IsNullOrEmpty(c.Path) ? "/" : c.Path,
            Expires = c.ExpiresAt is null ? -1 : new DateTimeOffset(c.ExpiresAt.Value).ToUnixTimeSeconds(),
            HttpOnly = c.HttpOnly,
            Secure = c.Secure,
            SameSite = c.SameSite switch
            {
                SameSitePolicy.Strict => SameSiteAttribute.Strict,
                SameSitePolicy.Lax => SameSiteAttribute.Lax,
                SameSitePolicy.None => SameSiteAttribute.None,
                _ => null,
            },
        };

        if (c.Name.StartsWith("__Host-", StringComparison.Ordinal))
        {
            // URL injection only: explicit Domain/Path attributes make
            // Chromium-family browsers (and the spec) reject a __Host- cookie.
            cookie.Url = $"https://{host}/";
        }
        else
        {
            cookie.Domain = c.Domain;
        }

        return cookie;
    }

    private static CookieItem ToCookieItem(BrowserContextCookiesResult c) => new()
    {
        Domain = c.Domain ?? string.Empty,
        Path = string.IsNullOrEmpty(c.Path) ? "/" : c.Path,
        Name = c.Name,
        Value = c.Value,
        Secure = c.Secure == true,
        HttpOnly = c.HttpOnly == true,
        SameSite = c.SameSite switch
        {
            SameSiteAttribute.Strict => SameSitePolicy.Strict,
            SameSiteAttribute.Lax => SameSitePolicy.Lax,
            SameSiteAttribute.None => SameSitePolicy.None,
            _ => SameSitePolicy.Unspecified,
        },
        ExpiresAt = c.Expires > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)c.Expires).UtcDateTime : null,
    };
}
