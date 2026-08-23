using System.Diagnostics;
using System.Text.Json;
using FlexFetch.Config;
using FlexFetch.Data;
using FlexFetch.Entities;
using FlexFetch.Enums;
using FlexFetch.Services;
using FlexFetch.Services.Routing;
using Microsoft.Playwright;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Services;

/// <summary>A single anti-detection check result (from the self-check).</summary>
public sealed record DetectionCheckResult(string Name, bool Passed, string Detail);

/// <summary>
/// Headless browser service with anti-detection: uses the system-installed
/// Chrome/Edge channel (never the bundled Chromium by default), injects
/// stealth, keeps a persistent profile, humanizes visits, and routes
/// through the same proxy policy as downloads.
/// </summary>
public sealed class StealthBrowserService : IAsyncDisposable
{
    private readonly IProxyService _proxy;
    private readonly CookiePoolService _cookiePool;
    private readonly IConfigRepository _config;
    private readonly ILogger _log;
    private readonly string _profileDir;
    private readonly int _idleMinutes;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _installLock = new(1, 1);
    private readonly Random _random = new();

    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private DateTime _lastAccess;
    private CancellationTokenSource? _idleCts;

    public StealthBrowserService(
        IProxyService proxy,
        CookiePoolService cookiePool,
        StorageService storage,
        IConfigRepository config,
        ILogger log)
    {
        _proxy = proxy;
        _cookiePool = cookiePool;
        _config = config;
        _log = log;
        _profileDir = Path.Combine(storage.DataDir, "profiles", "Chromium");
        _idleMinutes = 10;
    }

    /// <summary>Detected system browser executable, or null when none is found.</summary>
    public string? SystemBrowserPath { get; private set; }

    public string ProfileDir => _profileDir;

    /// <summary>True when the browser context is currently running.</summary>
    public bool IsRunning => _context is not null;

    /// <summary>Last activity time (for idle recycling).</summary>
    public DateTime LastAccess => _lastAccess;

    /// <summary>Returns the first existing path from candidates, or null.</summary>
    public static string? FindFirstExisting(IReadOnlyList<string> paths) =>
        paths.FirstOrDefault(File.Exists);

    /// <summary>True when a system browser (Chrome/Edge) is already available.</summary>
    public bool IsSystemBrowserDetected() =>
        FindFirstExisting(GetBrowserCandidates()) is not null;

    /// <summary>Candidate system browser paths per platform.</summary>
    public static IReadOnlyList<string> GetBrowserCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new List<string>
            {
                Path.Combine(local, @"Google\Chrome\Application\chrome.exe"),
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            };
            AddPlaywrightChromium(candidates, Path.Combine(local, "ms-playwright"), "chrome-win", "chrome.exe");
            return candidates;
        }

        if (OperatingSystem.IsLinux())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var candidates = new List<string>
            {
                "/usr/bin/google-chrome-stable",
                "/usr/bin/google-chrome",
                "/usr/bin/chromium-browser",
                "/usr/bin/chromium",
                "/usr/bin/microsoft-edge",
            };
            AddPlaywrightChromium(candidates, Path.Combine(home, ".cache", "ms-playwright"), "chrome-linux", "chrome");
            return candidates;
        }

        var macHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var macCandidates = new List<string>
        {
            "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
            "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
        };
        AddPlaywrightChromium(
            macCandidates,
            Path.Combine(macHome, "Library", "Caches", "ms-playwright"),
            Path.Combine("chrome-mac", "Chromium.app", "Contents", "MacOS"),
            "Chromium");
        return macCandidates;
    }

    /// <summary>
    /// Appends Playwright-installed Chromium executables (directories named
    /// chromium-*) under the given browsers root, so a Playwright-installed
    /// browser counts as available even without a system Chrome/Edge.
    /// </summary>
    private static void AddPlaywrightChromium(List<string> candidates, string browsersRoot, string subDir, string exeName)
    {
        if (!Directory.Exists(browsersRoot))
        {
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(browsersRoot, "chromium-*"))
        {
            candidates.Add(Path.Combine(dir, subDir, exeName));
        }
    }

    /// <summary>Determines whether the idle-recycle deadline has passed.</summary>
    public bool IsIdleDue(DateTime now) =>
        _context is not null && (now - _lastAccess).TotalMinutes >= _idleMinutes;

    /// <summary>Builds the JS that probes detection vectors (runs in the page).</summary>
    public static string BuildSelfCheckScript() => """
        () => {
          let cdc = false;
          try { for (const k in window) { if (k.startsWith('cdc_')) { cdc = true; break; } } } catch (e) {}
          const brands = navigator.userAgentData ? navigator.userAgentData.brands : null;
          return JSON.stringify({
            webdriver: navigator.webdriver,
            cdc: cdc,
            userAgent: navigator.userAgent,
            brands: brands ? brands.map(b => b.brand).join(',') : null,
            hardwareConcurrency: navigator.hardwareConcurrency,
            windowSize: window.outerWidth + 'x' + window.outerHeight,
            plugins: navigator.plugins ? navigator.plugins.length : -1,
            languages: navigator.languages ? navigator.languages.join(',') : null
          });
        }
        """;

    /// <summary>Parses the self-check JSON into results (pure logic, unit-testable).</summary>
    public static IReadOnlyList<DetectionCheckResult> ParseSelfCheckResults(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var results = new List<DetectionCheckResult>();

        var webdriver = root.GetProperty("webdriver").GetBoolean();
        results.Add(new DetectionCheckResult("navigator.webdriver", !webdriver, webdriver.ToString()));

        var cdc = root.GetProperty("cdc").GetBoolean();
        results.Add(new DetectionCheckResult("cdc_ marker", !cdc, cdc.ToString()));

        var ua = root.GetProperty("userAgent").GetString() ?? string.Empty;
        var uaClean = !ua.Contains("HeadlessChrome", StringComparison.OrdinalIgnoreCase)
            && !ua.Contains("PhantomJS", StringComparison.OrdinalIgnoreCase);
        results.Add(new DetectionCheckResult("User-Agent", uaClean, ua));

        var brands = root.GetProperty("brands").GetString() ?? string.Empty;
        var brandsClean = !brands.Contains("HeadlessChrome", StringComparison.OrdinalIgnoreCase);
        results.Add(new DetectionCheckResult("brands", brandsClean, brands));

        var hw = root.GetProperty("hardwareConcurrency").GetInt32();
        results.Add(new DetectionCheckResult("hardwareConcurrency", hw > 1, hw.ToString()));

        var windowSize = root.GetProperty("windowSize").GetString() ?? string.Empty;
        results.Add(new DetectionCheckResult("window size", windowSize == "1366x768", windowSize));

        var plugins = root.GetProperty("plugins").GetInt32();
        results.Add(new DetectionCheckResult("plugins", plugins >= 0, plugins.ToString()));

        var languages = root.GetProperty("languages").GetString() ?? string.Empty;
        results.Add(new DetectionCheckResult("languages", !string.IsNullOrEmpty(languages), languages));

        return results;
    }

    /// <summary>Opens a new page, initializing the browser on first use.</summary>
    public async Task<IPage> NewPageAsync()
    {
        await EnsureInitializedAsync();
        _lastAccess = DateTime.UtcNow;
        return await _context!.NewPageAsync();
    }

    /// <summary>Runs the anti-detection self-check and returns per-vector results.</summary>
    public async Task<IReadOnlyList<DetectionCheckResult>> SelfCheckAsync()
    {
        var page = await NewPageAsync();
        try
        {
            var json = await page.EvaluateAsync<string>(BuildSelfCheckScript());
            return ParseSelfCheckResults(json);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    /// <summary>Exports the browser's cookies for the given URLs (cookie channel read).</summary>
    public async Task<IReadOnlyList<CookieItem>> GetCookiesAsync(params string[] urls)
    {
        var page = await NewPageAsync();
        try
        {
            var cookies = await _context!.CookiesAsync(urls);
            return cookies.Select(c => ToCookieItem(c)).ToList();
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    /// <summary>Injects cookies into the browser context (cookie channel write).</summary>
    public async Task PushCookiesAsync(IEnumerable<CookieItem> cookies)
    {
        await EnsureInitializedAsync();
        await _context!.AddCookiesAsync(cookies.Select(ToPlaywrightCookie).ToArray());
    }

    /// <summary>
    /// Humanized group refresh: injects the group's cookies, visits each site
    /// with random waits and scrolling, then exports the updated cookies and
    /// writes them back into the centralized pool.
    /// </summary>
    public async Task RefreshGroupAsync(CookieGroup group)
    {
        if (group.Urls.Count == 0)
        {
            return;
        }

        await PushCookiesAsync(group.Cookies);
        var page = await NewPageAsync();
        try
        {
            var picks = group.Urls.OrderBy(_ => _random.Next()).Take(Math.Min(3, group.Urls.Count)).ToList();
            foreach (var url in picks)
            {
                try
                {
                    await page.GotoAsync(url, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 30_000,
                    });
                    await page.WaitForTimeoutAsync(_random.Next(3000, 8000));
                    await page.Mouse.WheelAsync(0, _random.Next(200, 800));
                    await page.WaitForTimeoutAsync(_random.Next(1500, 4000));
                }
                catch (Exception ex)
                {
                    _log.Warning("Group refresh visit to {Url} failed: {Message}", url, ex.Message);
                }
            }

            var exported = await _context!.CookiesAsync(group.Urls.ToArray());
            var items = exported.Select(c => ToCookieItem(c)).ToList();
            if (items.Count > 0)
            {
                _cookiePool.UpsertCookies(items);
                _log.Information("Group {Group} refresh exported {Count} cookies", group.Name, items.Count);
            }
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    /// <summary>Browser + stealth version info for the system info API.</summary>
    public async Task<string> GetVersionAsync()
    {
        await EnsureInitializedAsync();
        return $"Playwright {typeof(Playwright).Assembly.GetName().Version}";
    }

    /// <summary>
    /// Installs a Playwright browser only if no system browser was found.
    /// The install runs in a child process with the proxy passed through
    /// that process's environment, so the parent process is unaffected.
    /// Serialized so concurrent triggers (startup hosted service, browser
    /// startup) never start multiple installs at once.
    /// </summary>
    public async Task EnsureBrowserInstalledAsync(CancellationToken cancellationToken = default)
    {
        await _installLock.WaitAsync(cancellationToken);
        try
        {
            // Re-check under the lock: another caller may have installed a
            // system browser (or completed this install) while we waited.
            if (FindFirstExisting(GetBrowserCandidates()) is not null)
            {
                _log.Information("System browser detected; skipping browser install");
                return;
            }

            await InstallBrowserCoreAsync(cancellationToken);
        }
        finally
        {
            _installLock.Release();
        }
    }

    private async Task InstallBrowserCoreAsync(CancellationToken cancellationToken)
    {
        _log.Information("No system browser found; installing Playwright Chromium");
        var proxy = _config.Get(ConfigKeys.Proxy) ?? ConfigRegistry.GetDefault(ConfigKeys.Proxy);

        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows() && Environment.ProcessPath?.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) == false)
        {
            // On Windows the app host executable re-enters with the flag.
            startInfo.ArgumentList.Add("--install-browser");
        }
        else
        {
            // Running via `dotnet` (tests, Linux self-contained-less): pass the dll.
            startInfo.ArgumentList.Add(Environment.ProcessPath ?? "dotnet");
            startInfo.ArgumentList.Add("--install-browser");
        }

        if (!string.IsNullOrWhiteSpace(proxy))
        {
            startInfo.Environment["HTTPS_PROXY"] = proxy;
            startInfo.Environment["HTTP_PROXY"] = proxy;
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                _log.Warning("Failed to start browser installer process");
                return;
            }

            // Bound the install so a slow download cannot hang the service.
            var finished = await Task.WhenAny(
                process.WaitForExitAsync(cancellationToken),
                Task.Delay(TimeSpan.FromMinutes(30), cancellationToken));
            if (finished != process.WaitForExitAsync(cancellationToken) && !process.HasExited)
            {
                _log.Warning("Browser install timed out; killing installer process");
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            _log.Information("Playwright Chromium install exited with code {Code}", process.ExitCode);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Browser install failed");
        }
    }

    private async Task EnsureInitializedAsync()
    {
        if (_context is not null)
        {
            return;
        }

        await _initLock.WaitAsync();
        try
        {
            if (_context is not null)
            {
                return;
            }

            SystemBrowserPath = FindFirstExisting(GetBrowserCandidates());
            if (SystemBrowserPath is null)
            {
                // No system Chrome/Edge: install the Playwright browser before
                // launching (lazy install, scoped to a child process).
                await EnsureBrowserInstalledAsync();
            }

            _playwright = await Playwright.CreateAsync();

            var args = ManagedCode.Playwright.Stealth.PlaywrightStealthExtensions.StealthArgs.ToList();
            args.AddRange(new[]
            {
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-dev-shm-usage",
                "--ignore-certificate-errors",
                "--window-size=1366,768",
            });

            var options = new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = true,
                ExecutablePath = SystemBrowserPath,
                Args = args.ToArray(),
                IgnoreHTTPSErrors = true,
                // Headless Chromium reports window.outerWidth/Height from the
                // viewport (default 1280x720), so set it explicitly to match a
                // common desktop window and keep the self-check window-size
                // probe green.
                ViewportSize = new ViewportSize { Width = 1366, Height = 768 },
            };

            Directory.CreateDirectory(Path.GetDirectoryName(_profileDir)!);
            _context = await _playwright.Chromium.LaunchPersistentContextAsync(_profileDir, options);
            await ManagedCode.Playwright.Stealth.PlaywrightStealthExtensions.ApplyStealthAsync(_context);

            // Route browser traffic through the same proxy policy as downloads.
            await _context.RouteAsync("**/*", async route =>
            {
                var url = new Uri(route.Request.Url);
                var proxyUri = _proxy.GetProxyUri(url);
                if (proxyUri is null)
                {
                    await route.ContinueAsync();
                    return;
                }

                using var client = new HttpClient(_proxy.CreateHandler(url)) { Timeout = TimeSpan.FromSeconds(60) };
                await ForwardViaProxyAsync(route, client, proxyUri);
            });

            _lastAccess = DateTime.UtcNow;
            StartIdleRecycler();
            _log.Information("Stealth browser started ({Browser})", SystemBrowserPath ?? "playwright-default");
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static async Task ForwardViaProxyAsync(IRoute route, HttpClient client, string proxyUri)
    {
        var request = route.Request;
        using var forward = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
        foreach (var header in request.Headers)
        {
            forward.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.PostDataBuffer is not null && request.Method is "POST" or "PUT" or "PATCH")
        {
            forward.Content = new ByteArrayContent(request.PostDataBuffer);
        }

        try
        {
            using var response = await client.SendAsync(forward);
            var body = await response.Content.ReadAsByteArrayAsync();
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = (int)response.StatusCode,
                ContentType = response.Content.Headers.ContentType?.ToString(),
                Headers = response.Headers.ToDictionary(h => h.Key, h => h.Value.First()),
                BodyBytes = body,
            });
        }
        catch
        {
            await route.AbortAsync();
        }
    }

    private void StartIdleRecycler()
    {
        _idleCts?.Cancel();
        _idleCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_idleCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), _idleCts.Token);
                    if (IsIdleDue(DateTime.UtcNow))
                    {
                        _log.Information("Stealth browser idle, recycling");
                        await CloseAsync();
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Recycler superseded or service disposed.
            }
        });
    }

    /// <summary>Closes the browser context and releases the profile lock.</summary>
    public async Task CloseAsync()
    {
        await _initLock.WaitAsync();
        try
        {
            if (_context is not null)
            {
                await _context.CloseAsync();
                _context = null;
            }
            _playwright?.Dispose();
            _playwright = null;
        }
        finally
        {
            _initLock.Release();
        }
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

    private static Microsoft.Playwright.Cookie ToPlaywrightCookie(CookieItem c) => new()
    {
        Name = c.Name,
        Value = c.Value,
        Domain = c.Domain,
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

    public async ValueTask DisposeAsync() => await CloseAsync();
}
