using System.Net;
using System.Text.RegularExpressions;
using FlexFetch.Entities;
using FlexFetch.Services.Downloaders;
using FlexFetch.Services.Routing;
using Serilog;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Services.Session;

/// <summary>Result of a Twitter session import attempt.</summary>
public sealed record TwitterImportResult(
    bool Ok,
    TwitterSessionHealth Health,
    string? Error,
    int Parsed,
    int Stored,
    string? Account);

/// <summary>
/// Import and verification pipeline for the X (Twitter) session. The import
/// gate mirrors the YouTube one: a pasted jar is only accepted when it
/// carries an auth_token AND x.com answers it with a live identity (fresh
/// ct0/twid cookies) - otherwise the stored session stays untouched. There
/// is no browser pipeline and no periodic canary: an auth_token either works
/// or was revoked, so verification runs at import, on demand and lazily
/// when the downloader hits an auth failure.
/// </summary>
public sealed partial class TwitterSessionService
{
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(60);

    private readonly TwitterSessionStore _store;
    private readonly IProxyService _proxy;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Injectable HTTP runner (tests); defaults to a proxied GET
    /// that returns the (unconsumed) response. The handler must keep
    /// UseCookies=false so Set-Cookie stays visible in the headers.</summary>
    private readonly Func<string, IReadOnlyDictionary<string, string>?, CancellationToken, Task<HttpResponseMessage>> _send;

    private readonly SessionPipeline _pipeline = new("A Twitter session import or check is already running");

    public TwitterSessionService(
        TwitterSessionStore store,
        IProxyService proxy,
        ILogger log,
        Func<string, IReadOnlyDictionary<string, string>?, CancellationToken, Task<HttpResponseMessage>>? send = null)
    {
        _store = store;
        _proxy = proxy;
        _log = log;
        _send = send ?? DefaultSendAsync;
    }

    /// <summary>
    /// Imports a pasted jar (Netscape, browser JSON export or Set-Cookie):
    /// parse, filter to the x.com/twitter.com domains, require auth_token,
    /// then verify the identity live before the session file is replaced.
    /// </summary>
    public Task<TwitterImportResult> ImportAsync(string text, CancellationToken cancellationToken = default) =>
        _pipeline.RunAsync(ct => ImportCoreAsync(text, ct), cancellationToken);

    /// <summary>
    /// Re-verifies the stored token against x.com and records the verdict.
    /// Returns the resulting health; Unknown with an error when nothing is
    /// stored.
    /// </summary>
    public Task<TwitterSessionHealth> CheckAsync(CancellationToken cancellationToken = default) =>
        _pipeline.RunAsync(ct => CheckCoreAsync(ct), cancellationToken);

    private async Task<TwitterImportResult> ImportCoreAsync(string text, CancellationToken cancellationToken)
    {
        var parse = CookieTextParser.Parse(text, null);
        if (parse.Cookies.Count == 0)
        {
            return new TwitterImportResult(false, TwitterSessionHealth.Error,
                $"No cookies recognized: {string.Join("; ", parse.Errors)}", 0, 0, null);
        }

        var filtered = TwitterSessionStore.FilterTwitterDomain(parse.Cookies);
        if (filtered.Count == 0)
        {
            return new TwitterImportResult(false, TwitterSessionHealth.Error,
                "No x.com/twitter.com cookies in the import - only the x.com domain is imported", parse.Cookies.Count, 0, null);
        }

        if (!filtered.Any(c => c.Name.Equals("auth_token", StringComparison.OrdinalIgnoreCase)))
        {
            return new TwitterImportResult(false, TwitterSessionHealth.Error,
                "No auth_token in the import - log in at x.com and export the cookies of the x.com domain",
                parse.Cookies.Count, 0, null);
        }

        var check = await VerifyIdentityAsync(filtered, cancellationToken);
        if (!check.Ok)
        {
            // The verdict is about the PASTED token; the stored session (if
            // any) stays untouched - a bad re-import must not flip it.
            return new TwitterImportResult(false, TwitterSessionHealth.Invalid, check.Error, parse.Cookies.Count, 0, null);
        }

        // Refreshed ct0/twid win over the pasted values (freshest tickets);
        // the identity cookies are then cloned to the sibling domain so the
        // session serves twitter.com links as well as x.com.
        var merged = TwitterSessionStore.MirrorSiblingDomain(TwitterSessionStore.Merge(filtered, check.Cookies!));
        var meta = _store.ReadMeta() ?? new TwitterSessionMeta();
        meta.Account = check.Account ?? meta.Account;
        meta.UserId = check.UserId ?? meta.UserId;
        meta.ImportedAt = DateTime.UtcNow;
        meta.LastCheckAt = DateTime.UtcNow;
        meta.Health = TwitterSessionHealth.Ok.ToString();
        meta.LastError = null;
        _store.Write(merged, meta);
        _log.Information("Twitter session import accepted: {Stored} cookies stored for account {Account}",
            merged.Count, meta.Account ?? meta.UserId ?? "(unknown)");
        return new TwitterImportResult(true, TwitterSessionHealth.Ok, null, parse.Cookies.Count, merged.Count, meta.Account);
    }

    private async Task<TwitterSessionHealth> CheckCoreAsync(CancellationToken cancellationToken)
    {
        var jar = _store.ReadCookies();
        if (jar.Count == 0)
        {
            return TwitterSessionHealth.Unknown;
        }

        var check = await VerifyIdentityAsync(jar, cancellationToken);
        var health = check.Ok ? TwitterSessionHealth.Ok : TwitterSessionHealth.Invalid;
        _store.UpdateMeta(m =>
        {
            m.LastCheckAt = DateTime.UtcNow;
            m.Health = health.ToString();
            m.LastError = check.Error;
            if (check.Ok)
            {
                m.Account ??= check.Account;
                m.UserId ??= check.UserId;
            }
        });
        if (!check.Ok)
        {
            _log.Warning("Twitter session check failed: {Error}", check.Error);
        }

        return health;
    }

    private async Task<(bool Ok, string? Error, IReadOnlyList<CookieItem>? Cookies, string? UserId, string? Account)>
        VerifyIdentityAsync(IReadOnlyList<CookieItem> jar, CancellationToken cancellationToken)
    {
        var authToken = jar.First(c => c.Name.Equals("auth_token", StringComparison.OrdinalIgnoreCase)).Value;
        var headers = new Dictionary<string, string> { ["Cookie"] = $"auth_token={authToken}" };
        HttpResponseMessage response;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CheckTimeout);
            response = await _send("https://x.com/", headers, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, $"x.com did not answer within {CheckTimeout.TotalSeconds:F0}s", null, null, null);
        }
        catch (Exception ex)
        {
            return (false, $"x.com request failed: {ex.Message}", null, null, null);
        }

        string html;
        IReadOnlyList<string> setCookies;
        using (response)
        {
            try
            {
                html = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                return (false, $"reading the x.com response failed: {ex.Message}", null, null, null);
            }

            setCookies = response.Headers.TryGetValues("Set-Cookie", out var values)
                ? values.ToList()
                : new List<string>();
        }

        // The proof of a live session is the server SETTING ct0 (csrf) and
        // twid (account id) for the presented auth_token. Without them the
        // token was revoked or never carried a session.
        var ct0 = TwitterSessionStore.ExtractSetCookieValue(setCookies, "ct0");
        var twid = TwitterSessionStore.ExtractSetCookieValue(setCookies, "twid");
        if (ct0 is null || twid is null)
        {
            return (false, $"x.com rejected the auth_token (HTTP {(int)response.StatusCode}, no ct0/twid) - export fresh cookies",
                null, null, null);
        }

        var userId = Uri.UnescapeDataString(twid);
        if (userId.StartsWith("u=", StringComparison.Ordinal))
        {
            userId = userId[2..];
        }

        var account = ScreenNameRegex().Match(html) is { Success: true } m ? m.Groups[1].Value : null;

        // Persist the refreshed tickets over whatever was stored before.
        var refreshed = new List<CookieItem>();
        foreach (var name in new[] { "ct0", "twid", "auth_token" })
        {
            var value = name == "auth_token" ? authToken : TwitterSessionStore.ExtractSetCookieValue(setCookies, name);
            if (value is not null)
            {
                refreshed.Add(new CookieItem { Domain = ".x.com", Path = "/", Name = name, Value = value, Secure = true, HttpOnly = true });
            }
        }

        return (true, null, refreshed, userId, account);
    }

    private async Task<HttpResponseMessage> DefaultSendAsync(
        string url, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        var uri = new Uri(url);
        var handler = _proxy.CreateHandler(uri);
        using var client = new HttpClient(handler) { Timeout = CheckTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                client.DefaultRequestHeaders.Add(name, value);
            }
        }

        return await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    [GeneratedRegex("\"screen_name\":\"([A-Za-z0-9_]{1,20})\"")]
    private static partial Regex ScreenNameRegex();
}
