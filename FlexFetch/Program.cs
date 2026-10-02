using System.Text;
using FlexFetch.Api;
using FlexFetch.Config;
using FlexFetch.Data;
using FlexFetch.Enums;
using FlexFetch.HostedServices;
using FlexFetch.Services;
using FlexFetch.Services.Downloaders;
using FlexFetch.Services.Routing;
using FlexFetch.Services.Session;
using FlexFetch.Services.Tasks;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Serilog;
using ILogger = Serilog.ILogger;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

// Child-process entry used by the startup service to install the headless
// browser: runs Playwright's OS-dependency and install commands and exits
// without starting the web application.
if (args.Length > 0 && args[0] == "--install-browser")
{
    var browserName = args.Length > 1 ? args[1] : "firefox";
    // The parent process normally exports the browser cache location; a
    // manually invoked child resolves it from the data directory itself.
    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(FirefoxBrowserService.BrowsersPathEnv)))
    {
        var childDataDir = Program.ResolveDataDir(
            new ConfigurationBuilder().AddEnvironmentVariables().Build());
        Environment.SetEnvironmentVariable(
            FirefoxBrowserService.BrowsersPathEnv,
            Path.Combine(Path.GetFullPath(childDataDir), "components", "ms-playwright"));
    }

    var exitCode = 0;
    foreach (var step in BrowserInstaller.Steps(browserName, OperatingSystem.IsWindows()))
    {
        if (step[0] == "install-deps")
        {
            // apt only reads the lowercase proxy variables, and its fetches
            // are the least reliable when driven by environment variables
            // alone - mirror them and pin the proxy in apt's own config.
            BrowserInstaller.MirrorProxyEnvToLowerCase();
            BrowserInstaller.WriteAptProxyConf(
                Environment.GetEnvironmentVariable("HTTP_PROXY"),
                OperatingSystem.IsWindows());
            // Plain http to the Ubuntu archives breaks through restrictive
            // proxies; upgrade to https, or a mirror when one is configured.
            BrowserInstaller.PrepareAptSources(
                Environment.GetEnvironmentVariable(BrowserInstaller.AptMirrorEnv),
                OperatingSystem.IsWindows());
        }

        exitCode = Microsoft.Playwright.Program.Main(step);
        if (exitCode != 0)
        {
            break;
        }
    }

    if (exitCode == 0 && browserName == "firefox")
    {
        // Mark the installed build with this package's version so the
        // firefox readiness check can detect a future package/build skew.
        FirefoxBrowserService.WriteFirefoxBuildMarker();
    }

    Environment.Exit(exitCode);
}

var builder = WebApplication.CreateBuilder(args);

// Data directory: hidden runtime folder (.flexfetch) holding the database,
// logs, browser profiles, external components and route files. Container
// deployments mount the persistent volume at /data, which the app adopts
// without an environment variable.
var dataDir = ResolveDataDir(builder.Configuration);
Directory.CreateDirectory(dataDir);
Directory.CreateDirectory(Path.Combine(dataDir, "logs"));

// Self-managed components live under the data directory (in containers the
// volume survives recreation, so nothing re-downloads): point Playwright's
// browser cache into components/ms-playwright unless the deployment pinned
// it explicitly.
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(FirefoxBrowserService.BrowsersPathEnv)))
{
    Environment.SetEnvironmentVariable(
        FirefoxBrowserService.BrowsersPathEnv,
        Path.Combine(Path.GetFullPath(dataDir), "components", "ms-playwright"));
}

// yt-dlp resolves its JS runtime (deno - n-challenge solving and the bgutil
// PO token script provider) through PATH alone on Linux; the frozen-binary
// directory shortcut only exists on Windows. The data directory is never on
// PATH in deployments, so export it here: every spawned child (yt-dlp and
// the deno it launches for PO tokens) inherits the parent environment.
// Applied before any component exists - an entry without files is harmless.
Environment.SetEnvironmentVariable("PATH", YtdlpService.AppendToPathValue(
    Environment.GetEnvironmentVariable("PATH"),
    Path.Combine(Path.GetFullPath(dataDir), "components")));

// Editable runtime configuration on the data directory: seeded from the
// bundled template on first start and loaded last, so volume-persistent
// user edits override appsettings*.json without touching the image. A
// malformed user edit is moved aside (never loaded, never destroyed) and
// re-seeded, so a typo in the config cannot keep the web server from
// starting.
UserConfigFile.Seed(dataDir, AppContext.BaseDirectory);
var userConfigPath = UserConfigFile.PathFor(dataDir);
if (!UserConfigFile.TryValidate(userConfigPath, out var configProblem) && configProblem is not null)
{
    var aside = userConfigPath + ".invalid-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
    File.Move(userConfigPath, aside);
    Console.WriteLine($"User configuration {userConfigPath} is invalid ({configProblem}); moved to {aside} and re-seeded.");
    UserConfigFile.Seed(dataDir, AppContext.BaseDirectory);
}

// https endpoints whose certificate cannot be used are dropped from the
// configuration (giving up https) instead of failing the whole service at
// bind time; the http-endpoint default below only fires afterwards when no
// endpoints survive.
foreach (var notice in UserConfigFile.RemoveUnusableHttpsEndpoints(userConfigPath))
{
    Console.WriteLine($"Kestrel: {notice}");
}

// Older or hand-written configuration files without a Kestrel section must
// not leave the container without a listener.
UserConfigFile.EnsureHttpEndpoint(userConfigPath);
builder.Configuration.AddJsonFile(userConfigPath, optional: true, reloadOnChange: true);

// Configure Serilog: structured logging to console and rolling files
// (file output can be disabled via Logging:WriteToFile, e.g. in tests).
var loggerConfig = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console();
if (builder.Configuration.GetValue("Logging:WriteToFile", true))
{
    loggerConfig = loggerConfig.WriteTo.File(
        path: Path.Combine(dataDir, "logs", "flexfetch-.log"),
        rollingInterval: RollingInterval.Day,
        fileSizeLimitBytes: 100 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
}

Log.Logger = loggerConfig.CreateLogger();

builder.Host.UseSerilog();

// Data layer: one shared LiteDB store instance.
builder.Services.AddSingleton(new LiteDbStore(Path.Combine(dataDir, "flexfetch.db")));
builder.Services.AddSingleton<IUserRepository, UserRepository>();
builder.Services.AddSingleton<ITaskRepository, TaskRepository>();
builder.Services.AddSingleton<IShareRepository, ShareRepository>();
builder.Services.AddSingleton<IGuestRepository, GuestRepository>();

// Application services.
builder.Services.AddSingleton<UserService>();
builder.Services.AddSingleton<GuestSessionService>();
builder.Services.AddSingleton(Log.Logger);

// Storage + proxy + downloader pipeline.
builder.Services.AddSingleton(new StorageService(dataDir));
builder.Services.AddSingleton<IProxyService>(sp => new ProxyService(
    builder.Configuration,
    sp.GetRequiredService<ILogger>(),
    dataDir));
builder.Services.AddSingleton(sp => new YtdlpService(
    sp.GetRequiredService<IProxyService>(),
    sp.GetRequiredService<ILogger>(),
    dataDir));

// Session maintenance: the Firefox session source, the snapshot store (the
// single source of truth for the YouTube login session), the InnerTube probe
// and the export pipeline (the only snapshot writer).
builder.Services.AddSingleton<SessionSnapshotService>();
builder.Services.AddSingleton<FirefoxBrowserService>();
builder.Services.AddSingleton<SessionProbeService>();
builder.Services.AddSingleton<SessionExportService>();
builder.Services.AddSingleton<SessionDiagnosisService>();
builder.Services.AddSingleton<PotProviderService>();

// Downloader plugins are self-discovered via reflection; no manual registration.
builder.Services.AddSingleton(sp => DownloaderFactory.Create(sp));
builder.Services.AddSingleton<ITaskExecutor>(sp => new DownloaderTaskExecutor(
    sp.GetRequiredService<DownloaderFactory>(),
    sp.GetRequiredService<IProxyService>(),
    sp.GetRequiredService<ILogger>(),
    (parent, child, referrer) =>
    {
        var taskService = sp.GetRequiredService<TaskService>();
        return taskService.Submit(parent.OwnerUserId, child.Url, downloaderType: child.DownloaderType,
            parentId: parent.Id, title: child.Title, referrer: referrer);
    }));
builder.Services.AddSingleton<TaskService>();

// Background periodic services. Session maintenance and the startup task
// service are registered as concrete singletons first so other services can
// inject them (the session status API reads maintenance; the pot supervisor
// and maintenance await the startup component plan).
builder.Services.AddSingleton<SessionMaintenanceHostedService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SessionMaintenanceHostedService>());
builder.Services.AddSingleton<StartupTasksHostedService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StartupTasksHostedService>());
builder.Services.AddHostedService<DependencyUpgradeHostedService>();
builder.Services.AddHostedService<PotSupervisorHostedService>();
builder.Services.AddHostedService<CleanupHostedService>();

// The auth cookie is protected by the Data Protection key ring; keep it in
// the data dir (volume-backed) so cookies survive container recreation -
// keys left in the container layer die with the image update and force
// every logged-in browser to re-login.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));

// Server-side session cookie authentication.
var sessionHours = int.Parse(ConfigRegistry.From(builder.Configuration, ConfigKeys.SessionHours));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "FlexFetch.Auth";
        options.ExpireTimeSpan = TimeSpan.FromHours(sessionHours);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Admin", p => p.RequireRole(nameof(UserRole.Admin)))
    // App functionality (task API): authenticated users, or anonymous
    // visitors when account.allowAnonymous is enabled (shared guest pool).
    .AddPolicy("AppAccess", p => p.AddRequirements(new AppAccessRequirement()));
builder.Services.AddSingleton<IAuthorizationHandler, AppAccessHandler>();

// Production: response compression + HTTPS redirection.
builder.Services.AddResponseCompression(options => options.EnableForHttps = true);

var app = builder.Build();

app.UseResponseCompression();
if (!app.Environment.IsDevelopment())
{
    // http traffic redirects to the https endpoint actually bound by the
    // server (IServerAddressesFeature): when https failed to start
    // (unusable certificate, port taken) or is not configured at all,
    // requests stay on http instead of hitting a dead redirect target.
    var serverFeatures = app.Services.GetRequiredService<IServer>().Features;
    app.Use(async (context, next) =>
    {
        if (context.Request.Scheme == "http")
        {
            var httpsAddress = serverFeatures.Get<IServerAddressesFeature>()?.Addresses
                .FirstOrDefault(a => a.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
            if (httpsAddress is not null)
            {
                context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
                context.Response.Headers.Location =
                    $"https://{context.Request.Host.Host}:{new Uri(httpsAddress).Port}{context.Request.Path}{context.Request.QueryString}";
                return;
            }
        }

        await next(context);
    });
}

app.UseAuthentication();
app.UseAuthorization();

AuthApi.Map(app);
UsersApi.Map(app);
TasksApi.Map(app);
ShareApi.Map(app);
SessionApi.Map(app);
SystemApi.Map(app);

// Static web UI: "/" serves wwwroot/index.html via UseDefaultFiles.
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        if (builder.Environment.IsDevelopment())
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            ctx.Context.Response.Headers.Pragma = "no-cache";
        }
    },
});

// Distinguish an API-triggered shutdown (SystemApi) from Ctrl+C / host
// signals, so the stop log line identifies who asked the process to stop.
app.Lifetime.ApplicationStopping.Register(() =>
{
    Log.Information(Program.ShutdownByApi
        ? "Application stopping: graceful shutdown requested via system page"
        : "Application stopping: interrupted by Ctrl+C or host signal");
});

app.Run();

// Expose the generated Program class for integration tests (WebApplicationFactory).
public partial class Program
{
    /// <summary>Process start time (UTC), surfaced by the system info API.</summary>
    public static readonly DateTime StartedAt = DateTime.UtcNow;

    /// <summary>True when shutdown was requested via the system API, so the
    /// stop log can distinguish it from Ctrl+C / host signals.</summary>
    public static volatile bool ShutdownByApi;

    /// <summary>
    /// Resolves the data directory: an explicit storage.dataDir wins;
    /// without one, the /data mount is adopted when it exists (container
    /// deployments map the persistent volume there - no environment
    /// variable needed), else the default hidden .flexfetch folder.
    /// </summary>
    public static string ResolveDataDir(IConfiguration configuration)
    {
        var configured = ConfigRegistry.From(configuration, ConfigKeys.DataDir);
        return configured != ConfigRegistry.GetDefault(ConfigKeys.DataDir)
            || !OperatingSystem.IsLinux()
            || !Directory.Exists("/data")
            ? configured
            : "/data";
    }
}
