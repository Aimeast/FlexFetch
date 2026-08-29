using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FlexFetch.Config;
using FlexFetch.Data;
using FlexFetch.Entities;
using FlexFetch.Enums;
using FlexFetch.Services;
using FlexFetch.Services.Refresh;
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
    private readonly IConfiguration _config;
    private readonly ILogger _log;
    private readonly string _profileDir;
    private readonly int _idleMinutes;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _installLock = new(1, 1);
    private readonly Random _random = new();
    private readonly Func<IEnumerable<ICookieRefreshStrategy>> _strategySource;
    private readonly ICookieRefreshStrategy _default = new DefaultCookieRefreshStrategy();

    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private HttpClient? _browserHttpClient;
    private DateTime _lastAccess;
    private CancellationTokenSource? _idleCts;

    public StealthBrowserService(
        IProxyService proxy,
        CookiePoolService cookiePool,
        StorageService storage,
        IConfiguration config,
        ILogger log,
        Func<IEnumerable<ICookieRefreshStrategy>> strategySource)
    {
        _proxy = proxy;
        _cookiePool = cookiePool;
        _config = config;
        _log = log;
        _profileDir = Path.Combine(storage.DataDir, "profiles", "Chromium");
        _idleMinutes = 10;
        _strategySource = strategySource;
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

    /// <summary>
    /// Builds the persistent-context launch options: system browser
    /// executable, stealth arguments, headless viewport and service-worker
    /// blocking. Blocking service workers is required for route interception:
    /// a service worker registered in the persistent profile (e.g. YouTube's)
    /// intercepts requests inside the browser, so Playwright's route handlers
    /// never see those requests and the proxy routing is silently bypassed.
    /// </summary>
    public static BrowserTypeLaunchPersistentContextOptions BuildLaunchOptions(string? executablePath)
    {
        var args = ManagedCode.Playwright.Stealth.PlaywrightStealthExtensions.StealthArgs.ToList();
        args.AddRange(new[]
        {
            "--no-sandbox",
            "--disable-setuid-sandbox",
            "--disable-dev-shm-usage",
            "--ignore-certificate-errors",
            "--window-size=1366,768",
        });

        return new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = true,
            ExecutablePath = executablePath,
            Args = args.ToArray(),
            IgnoreHTTPSErrors = true,
            // Headless Chromium reports window.outerWidth/Height from the
            // viewport (default 1280x720), so set it explicitly to match a
            // common desktop window and keep the self-check window-size
            // probe green.
            ViewportSize = new ViewportSize { Width = 1366, Height = 768 },
            // Block new service-worker registrations; existing ones are
            // removed from the profile by CleanStaleServiceWorkers.
            ServiceWorkers = ServiceWorkerPolicy.Block,
        };
    }

    /// <summary>
    /// Removes the service-worker store from the browser profile. A stale
    /// service worker registered by a previous session intercepts requests
    /// inside the browser, so Playwright's route handlers never see those
    /// requests and the proxy routing is silently bypassed (requests then
    /// fail natively). The store is pure cache/registration state, so it is
    /// safe to delete while the browser is not running; new registrations
    /// are blocked by BuildLaunchOptions.
    /// </summary>
    public static void CleanStaleServiceWorkers(string profileDir)
    {
        var dir = Path.Combine(profileDir, "Default", "Service Worker");
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
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
        await _context!.AddCookiesAsync(cookies.SelectMany(MaterializeShared).ToArray());
    }

    /// <summary>
    /// Materializes a cookie into one Playwright cookie per domain it lives on
    /// (primary + shared sibling domains), so the browser jar matches a real one.
    /// </summary>
    private static IEnumerable<Microsoft.Playwright.Cookie> MaterializeShared(CookieItem c)
    {
        var domains = new List<string> { c.Domain };
        if (c.SharedDomains is { Count: > 0 })
        {
            domains.AddRange(c.SharedDomains);
        }

        foreach (var domain in domains.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return ToPlaywrightCookie(c, domain);
        }
    }

    /// <summary>Selects the refresh strategy for a group, falling back to the default.</summary>
    private ICookieRefreshStrategy SelectStrategy(CookieGroup group) =>
        _strategySource().FirstOrDefault(s => s.IsMatch(group)) ?? _default;

    /// <summary>Visible text of the page body, truncated for analysis.</summary>
    private static async Task<string?> SafeTextAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<string?>(
                "() => document.body ? document.body.innerText.slice(0, 20000) : null");
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>YouTube's reported login state (ytcfg.loggedIn), or null when unavailable.</summary>
    private static async Task<bool?> ReadLoggedInStateAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<bool?>(
                "() => { try { const d = window.ytcfg && window.ytcfg.data_; return (d && typeof d.loggedIn === 'boolean') ? d.loggedIn : null; } catch { return null; } }");
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// True when the page shows a "Sign in" button/link (YouTube's logged-out
    /// header), which ytcfg.loggedIn may not always report. Matches the sign-in
    /// link and YouTube's attributed-string span by their exact "Sign in" text.
    /// </summary>
    private static async Task<bool> ReadSignInButtonPresentAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<bool>(
                "() => { const els = document.querySelectorAll('a[href*=\"signin\"], a[href*=\"ServiceLogin\"], span.ytAttributedStringHost'); for (const e of els) { if (e.textContent && e.textContent.trim() === 'Sign in') return true; } return false; }");
        }
        catch (Exception)
        {
            return false;
        }
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

        // Site-specific checks (e.g. YouTube session rejection) come from the
        // strategy selected for this group; other sites use the default.
        var strategy = SelectStrategy(group);

        // Expose the in-progress state so the UI can show "Refreshing...".
        _cookiePool.MarkGroupRefreshing(group.Id);
        _log.Information("Group {Group} refresh started ({UrlCount} urls)", group.Name, group.Urls.Count);
        // Snapshot the pool state: cookies removed by the site (or expired)
        // will be diffed against the export and deleted from the pool, while
        // cookies written by concurrent tasks after this point are kept.
        var before = group.Cookies.ToList();
        try
        {
            // Push the group's cookies; shared cookies carry their sibling
            // domains (SharedDomains) and are materialized onto every domain
            // the session lives on by PushCookiesAsync.
            await PushCookiesAsync(group.Cookies);
            var page = await NewPageAsync();
            try
            {
                // Pick URLs to visit, forcing HTTPS: Secure / __Secure-* cookies
                // are only attached by the browser on HTTPS pages, so visiting
                // a plain http:// URL would present an anonymous session even
                // with a valid cookie jar (loggedIn=false).
                var picks = group.Urls
                    .Select(ToHttps)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(_ => _random.Next())
                    .Take(Math.Min(3, group.Urls.Count))
                    .ToList();
                var visited = 0;
                var sessionRejection = (string?)null;
                foreach (var url in picks)
                {
                    try
                    {
                        // Track InnerTube API auth failures during the visit: a
                        // 401/403 from youtubei/v1 means the presented session
                        // was rejected even if the page itself loads.
                        var apiAuthFailed = false;
                        void OnResponse(object? sender, IResponse e)
                        {
                            if (e.Url.Contains("youtubei/v1", StringComparison.OrdinalIgnoreCase)
                                && (e.Status == 401 || e.Status == 403))
                            {
                                apiAuthFailed = true;
                            }
                        }

                        page.Response += OnResponse;
                        var gotoOk = false;
                        try
                        {
                            await page.GotoAsync(url, new PageGotoOptions
                            {
                                WaitUntil = WaitUntilState.DOMContentLoaded,
                                // The page is fetched through the proxy tunnel,
                                // so give it more headroom than a direct visit.
                                Timeout = 60_000,
                            });
                            gotoOk = true;
                        }
                        catch (Exception ex)
                        {
                            _log.Warning("Group refresh visit to {Url} failed: {Message}", url, ex.Message);
                        }

                        if (gotoOk)
                        {
                            visited++;
                            await page.WaitForTimeoutAsync(_random.Next(3000, 8000));
                            await page.Mouse.WheelAsync(0, _random.Next(200, 800));
                            await page.WaitForTimeoutAsync(_random.Next(1500, 4000));
                        }

                        page.Response -= OnResponse;

                        // Navigation failed (e.g. net::ERR_FAILED): skip the
                        // humanized waits so the group fails promptly instead
                        // of idling for ~10+ seconds.
                        if (!gotoOk)
                        {
                            continue;
                        }

                        // Landing on a login / verification page means the
                        // presented session was not accepted: the refresh
                        // cannot renew it and must not fake success.
                        sessionRejection ??= strategy.GetSessionRejectionReason(page.Url);

                        // Bot checks are served in-page (the URL stays on
                        // youtube.com), so also inspect the page content, the
                        // reported login state and API auth failures.
                        if (sessionRejection is null)
                        {
                            var pageText = await SafeTextAsync(page);
                            var loggedIn = await ReadLoggedInStateAsync(page);
                            var signInButton = await ReadSignInButtonPresentAsync(page);
                            sessionRejection = strategy.GetPageContentRejectionReason(
                                new CookieRefreshPageSignals(pageText, loggedIn, apiAuthFailed, signInButton));
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Warning("Group refresh visit to {Url} failed: {Message}", url, ex.Message);
                    }
                }

                // Every visit failed (timeout/network): mark the group Failed
                // instead of exporting the stale pool cookies and faking Ok.
                if (visited == 0)
                {
                    _cookiePool.MarkGroupRefreshFailed(group.Id, "All refresh URLs failed");
                    _log.Warning("Group {Group} refresh failed: no URL could be visited", group.Name);
                    return;
                }

                // The site flagged the session (e.g. YouTube logged in=false):
                // a refresh cannot revive it. Fail honestly so the pool keeps
                // its old cookies and the user knows to re-export from a real
                // browser.
                if (sessionRejection is not null)
                {
                    _cookiePool.MarkGroupRefreshFailed(group.Id, sessionRejection);
                    _log.Warning("Group {Group} refresh failed: session rejected ({Reason})", group.Name, sessionRejection);
                    return;
                }

                // Export from all relevant domains, not just the group URLs:
                // identity cookies live on the .google.com root domain and
                // must be captured back, or the sync-delete would remove them.
                var exportUrls = BuildExportUrls(group.Urls, before);
                var exported = await _context!.CookiesAsync(exportUrls);
                var items = exported.Select(c => ToCookieItem(c)).ToList();

                // Identity-cookie retention check: the pool had session
                // cookies before the visit, but the browser no longer has any
                // of them -> the site rejected the session (flagged). Fail
                // without touching the pool, so the old cookies stay for the
                // user to re-export from a real browser.
                var exportRejection = strategy.GetSessionRejectionReason(before, items);
                if (exportRejection is not null)
                {
                    _cookiePool.MarkGroupRefreshFailed(group.Id, exportRejection);
                    _log.Warning("Group {Group} refresh failed: session rejected ({Reason})", group.Name, exportRejection);
                    return;
                }

                if (items.Count > 0)
                {
                    _cookiePool.UpsertCookies(items);
                    _log.Information("Group {Group} refresh exported {Count} cookies", group.Name, items.Count);
                }

                // Compare against the pre-refresh snapshot: which cookies were
                // actually renewed by this visit (vs unchanged from the pool).
                var newNames = items
                    .Where(i => !before.Any(b => SameKey(b, i)))
                    .Select(i => i.Name)
                    .Distinct()
                    .ToList();
                var changedNames = items
                    .Where(i => before.Any(b => SameKey(b, i) && b.Value != i.Value))
                    .Select(i => i.Name)
                    .Distinct()
                    .ToList();
                if (newNames.Count > 0 || changedNames.Count > 0)
                {
                    _log.Information("Group {Group} refresh renewed cookies new={New} changed={Changed}",
                        group.Name,
                        string.Join(",", newNames.Count > 0 ? newNames : new List<string> { "none" }),
                        string.Join(",", changedNames.Count > 0 ? changedNames : new List<string> { "none" }));
                }
                else
                {
                    _log.Information("Group {Group} refresh renewed cookies: none (cookies unchanged)", group.Name);
                }

                // Sync deletions: drop pool cookies that existed before the
                // refresh but are gone from the browser (site-removed or
                // expired); anything written concurrently is preserved.
                var removed = _cookiePool.SyncGroupCookies(group.Id, before, items);
                if (removed > 0)
                {
                    _log.Information("Group {Group} refresh removed {Count} deleted cookies", group.Name, removed);
                }

                _cookiePool.MarkGroupRefreshed(group.Id);
                _log.Information("Group {Group} refresh completed ({Count} cookies)", group.Name, items.Count);
            }
            finally
            {
                await page.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            _cookiePool.MarkGroupRefreshFailed(group.Id, ex.Message);
            _log.Warning("Group {Group} refresh failed: {Message}", group.Name, ex.Message);
            throw;
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
        var proxy = ConfigRegistry.From(_config, ConfigKeys.Proxy);

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

            var options = BuildLaunchOptions(SystemBrowserPath);

            // Route browser traffic through the same proxy policy as downloads.
            // Only requests that need the proxy are intercepted (predicate);
            // everything else is handled natively by the browser. The tunnel
            // HttpClient uses the configured proxy and auto-decompresses, so a
            // gzip/br page body forwarded to the browser stays intact.
            var proxyAddress = _proxy.GetBrowserProxyAddress();
            if (!string.IsNullOrWhiteSpace(proxyAddress))
            {
                _browserHttpClient = new HttpClient(new SocketsHttpHandler
                {
                    // ConnectCallback speaks SOCKS5 directly (ATYP=3 domain,
                    // remote DNS) instead of .NET's built-in SocksHandler,
                    // which can stall against some proxies.
                    ConnectCallback = Socks5ConnectCallbackAsync,
                    AutomaticDecompression = DecompressionMethods.All,
                    UseCookies = false,
                })
                {
                    // Short enough that an unresponsive proxy fails fast (log +
                    // direct retry) instead of hanging the navigation until
                    // Chromium gives up with net::ERR_FAILED.
                    Timeout = TimeSpan.FromSeconds(45),
                };

                // Probe the proxy once at startup so an outage shows up in the
                // log immediately instead of as an unexplained ERR_FAILED.
                await ProbeBrowserProxyAsync(proxyAddress);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_profileDir)!);
            // A stale service worker (e.g. YouTube's) in the persistent
            // profile intercepts requests inside the browser, so Playwright's
            // route handlers never see them and the proxy routing is silently
            // bypassed (navigation then fails natively). Remove the store
            // before launch; new registrations are blocked by the launch
            // options.
            CleanStaleServiceWorkers(_profileDir);
            _context = await _playwright.Chromium.LaunchPersistentContextAsync(_profileDir, options);
            await ManagedCode.Playwright.Stealth.PlaywrightStealthExtensions.ApplyStealthAsync(_context);

            if (_browserHttpClient is not null)
            {
                // Register after the context exists: RouteAsync on a null
                // context throws NullReferenceException. The Func<string,bool>
                // predicate overload routes per-URL through the proxy policy
                // (proxy vs direct), mirroring the HttpGetUrl reference
                // implementation. The predicate only calls the DNS-free fast
                // decision, so it never blocks Playwright's route handling;
                // every request it matches is forwarded by RouteHandler.
                _log.Information("Registering browser route (proxy tunnel available)");
                await _context.RouteAsync(ShouldRouteToProxy, RouteHandler);
                _log.Information("Browser route registered");
            }
            else
            {
                _log.Warning("Browser route NOT registered: proxy tunnel client is null");
            }

            _lastAccess = DateTime.UtcNow;
            StartIdleRecycler();
            _log.Information("Stealth browser started ({Browser})", SystemBrowserPath ?? "playwright-default");
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task RouteHandler(IRoute route)
    {
        if (_browserHttpClient is null)
        {
            // The route is only registered when the tunnel client exists;
            // guard anyway so a future re-registration never throws.
            await route.ContinueAsync();
            return;
        }

        // The predicate already applied the routing policy (proxy vs direct),
        // so every request reaching this handler is forwarded through the
        // proxy tunnel - mirroring the HttpGetUrl reference implementation,
        // which never re-checks or continues natively inside the handler.
        await ForwardViaProxyAsync(route, _browserHttpClient);
    }

    /// <summary>
    /// Quick reachability probe of the browser proxy (short timeout). An
    /// unresponsive proxy hangs the tunneled navigation until Chromium fails
    /// it with net::ERR_FAILED; probing surfaces the outage in the log
    /// immediately instead.
    /// </summary>
    private async Task ProbeBrowserProxyAsync(string proxyAddress)
    {
        try
        {
            using var probe = new HttpClient(new SocketsHttpHandler
            {
                ConnectCallback = Socks5ConnectCallbackAsync,
                UseCookies = false,
            })
            {
                Timeout = TimeSpan.FromSeconds(5),
            };

            using var response = await probe.GetAsync("https://www.gstatic.com/generate_204");
            _log.Information("Browser proxy probe: {Status}", (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Browser proxy unreachable: {Message} (refresh will fail until the proxy is back)", ex.Message);
        }
    }

    /// <summary>
    /// Route predicate for the browser context: decides per-URL whether the
    /// request must go through the proxy tunnel (proxy policy) or may be
    /// fetched natively by the browser. Uses the DNS-free fast routing
    /// decision only; matched requests are forwarded by RouteHandler, so no
    /// DNS resolution ever runs on the Playwright routing path.
    /// </summary>
    private bool ShouldRouteToProxy(string url)
    {
        var hit = _browserHttpClient is not null
            && Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
            && _proxy.ShouldProxyFast(parsed);
        return hit;
    }

    /// <summary>
    /// Builds the URL list for cookie export: the group's refresh URLs plus
    /// one URL per distinct cookie domain in the snapshot (primary domain and
    /// any shared sibling domains). YouTube identity cookies (SID/SAPISID/
    /// __Secure-1PSID) live on the .google.com root domain too, so exporting
    /// only the group URLs would miss them - and the sync-delete would then
    /// drop those identity cookies from the pool.
    /// </summary>
    private static string[] BuildExportUrls(IReadOnlyList<string> groupUrls, IReadOnlyList<CookieItem> before)
    {
        var urls = new List<string>(groupUrls.Select(ToHttps));
        var domains = before
            .SelectMany(c => new[] { c.Domain }.Concat(c.SharedDomains ?? new()))
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var domain in domains)
        {
            var host = domain.TrimStart('.');
            if (host.Length > 0 && Uri.TryCreate($"https://{host}/", UriKind.Absolute, out _))
            {
                urls.Add($"https://{host}/");
            }
        }

        return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Forces HTTPS on a URL: Secure and __Secure-* cookies are only attached
    /// by the browser on HTTPS pages, so an http:// visit would appear as an
    /// anonymous session even with valid cookies.
    /// </summary>
    private static string ToHttps(string url)
    {
        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? "https://" + url.Substring("http://".Length)
            : url;
    }

    /// <summary>
    /// Custom connect callback that speaks SOCKS5 directly (ATYP=3 domain
    /// name, remote resolution) with a bounded handshake timeout. .NET's
    /// built-in SocksHandler can stall against some proxies, which surfaced
    /// as an unexplained net::ERR_FAILED after ~21s.
    /// </summary>
    private async ValueTask<Stream> Socks5ConnectCallbackAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var proxyAddress = _proxy.GetBrowserProxyAddress();
        if (string.IsNullOrWhiteSpace(proxyAddress) || !Uri.TryCreate(proxyAddress, UriKind.Absolute, out var proxyUri))
        {
            throw new InvalidOperationException("Browser proxy is not configured");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(proxyUri.Host, proxyUri.Port, timeout.Token);
            var stream = tcp.GetStream();

            // Greeting: version 5, one method (no auth).
            await stream.WriteAsync(BuildSocks5Greeting(), timeout.Token);
            var greeting = new byte[2];
            await stream.ReadExactlyAsync(greeting, timeout.Token);
            if (greeting[0] != 0x05 || greeting[1] != 0x00)
            {
                throw new IOException($"SOCKS5 greeting rejected (method {greeting[1]})");
            }

            // CONNECT with the raw hostname: the proxy resolves DNS remotely
            // (ATYP=3), so local resolution cannot poison or hang the lookup.
            var connect = BuildSocks5ConnectRequest(context.DnsEndPoint.Host, (ushort)context.DnsEndPoint.Port);
            await stream.WriteAsync(connect, timeout.Token);

            var header = new byte[4];
            await stream.ReadExactlyAsync(header, timeout.Token);
            if (header[1] != 0x00)
            {
                throw new IOException($"SOCKS5 CONNECT failed (reply {header[1]})");
            }

            // Consume BND.ADDR (+ its length byte for domain) and BND.PORT.
            var addrLen = header[3] switch
            {
                0x01 => 4,
                0x04 => 16,
                0x03 => stream.ReadByte(),
                _ => throw new IOException($"SOCKS5 unexpected ATYP {header[3]}"),
            };
            if (addrLen < 0)
            {
                throw new IOException("SOCKS5 truncated BND.ADDR");
            }

            var tail = new byte[addrLen + 2];
            await stream.ReadExactlyAsync(tail, timeout.Token);
            return stream;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Builds the SOCKS5 greeting (version 5, one method: no auth).
    /// </summary>
    public static byte[] BuildSocks5Greeting() => new byte[] { 0x05, 0x01, 0x00 };

    /// <summary>
    /// Builds a SOCKS5 CONNECT request with ATYP=3 (domain name), so the proxy
    /// resolves the hostname remotely - equivalent to curl's socks5h. .NET's
    /// built-in SocksHandler can stall against some proxies, so we speak the
    /// protocol ourselves.
    /// </summary>
    public static byte[] BuildSocks5ConnectRequest(string host, ushort port)
    {
        var hostBytes = System.Text.Encoding.ASCII.GetBytes(host);
        var request = new byte[4 + 1 + hostBytes.Length + 2];
        request[0] = 0x05; // version
        request[1] = 0x01; // CONNECT
        request[2] = 0x00; // reserved
        request[3] = 0x03; // ATYP: domain name
        request[4] = (byte)hostBytes.Length;
        Buffer.BlockCopy(hostBytes, 0, request, 5, hostBytes.Length);
        request[^2] = (byte)(port >> 8);
        request[^1] = (byte)port;
        return request;
    }

    /// <summary>
    /// True when two cookies are the same key (domain+path+name).</summary>
    private static bool SameKey(CookieItem a, CookieItem b) =>
        string.Equals(NormalizeDomain(a.Domain), NormalizeDomain(b.Domain), StringComparison.Ordinal)
        && string.Equals(a.Path ?? "/", b.Path ?? "/", StringComparison.Ordinal)
        && string.Equals(a.Name, b.Name, StringComparison.Ordinal);

    private static string NormalizeDomain(string? domain)
    {
        var d = domain?.Trim().TrimStart('.').ToLowerInvariant() ?? string.Empty;
        return d;
    }

    private async Task ForwardViaProxyAsync(IRoute route, HttpClient client)
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
            var startedAt = Stopwatch.GetTimestamp();
            using var response = await client.SendAsync(forward);
            var body = await response.Content.ReadAsByteArrayAsync();
            var elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

            // Slow forwards reveal where time goes: a hang in the proxy
            // CONNECT phase (no response at all) vs a slow response. Logged
            // at Debug so normal operation stays quiet - this fires for every
            // slow tunneled request during a page load.
            if (elapsedMs > 2000)
            {
                _log.Debug(
                    "Proxy forward {Method} {Url} took {Elapsed} ms (status {Status})",
                    request.Method, request.Url, elapsedMs.ToString("0"), (int)response.StatusCode);
            }

            // Forward the full response; keep every value per header (the
            // browser needs all Set-Cookie entries, not just the first one).
            var headers = new List<KeyValuePair<string, string>>();
            foreach (var header in response.Headers)
            {
                if (IsHopByHop(header.Key))
                {
                    continue; // Connection, Transfer-Encoding, ... are for the
                    // forwarding tunnel only and must not reach the browser.
                }

                foreach (var value in header.Value)
                {
                    headers.Add(new KeyValuePair<string, string>(header.Key, value));
                }
            }

            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = (int)response.StatusCode,
                ContentType = response.Content.Headers.ContentType?.ToString(),
                Headers = headers,
                BodyBytes = body,
            });
        }
        catch (Exception ex)
        {
            // Abort instead of letting the browser retry the request
            // directly: this URL was routed here because the policy says it
            // must go through the proxy, and a direct retry bypasses the
            // proxy (the likely reason the forward failed), surfacing as a
            // slow net::ERR_FAILED. Matches the reference implementation.
            _log.Warning(ex, "Proxy forward failed for {Url}; aborting request", request.Url);
            await route.AbortAsync();
        }
    }

    private static bool IsHopByHop(string name) =>
        name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("TE", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Trailer", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);

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

    private static Microsoft.Playwright.Cookie ToPlaywrightCookie(CookieItem c, string domain) => new()
    {
        Name = c.Name,
        Value = c.Value,
        Domain = domain,
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
