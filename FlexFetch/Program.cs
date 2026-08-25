using FlexFetch.Api;
using FlexFetch.Config;
using FlexFetch.Data;
using FlexFetch.Entities;
using FlexFetch.Enums;
using FlexFetch.HostedServices;
using FlexFetch.Services;
using FlexFetch.Services.Downloaders;
using FlexFetch.Services.Refresh;
using FlexFetch.Services.Routing;
using FlexFetch.Services.Tasks;
using Microsoft.AspNetCore.Authentication.Cookies;
using Serilog;
using ILogger = Serilog.ILogger;

// Child-process entry used by DependencyInstallHostedService to install the
// headless browser: runs Playwright's install command and exits without
// starting the web application.
if (args.Length > 0 && args[0] == "--install-browser")
{
    Environment.Exit(Microsoft.Playwright.Program.Main(new[] { "install", "chromium" }));
}

var builder = WebApplication.CreateBuilder(args);

// Data directory: hidden runtime folder (.flexfetch) holding the database,
// logs, browser profiles, external components and route files.
var dataDir = builder.Configuration["Data:Dir"] ?? ConfigRegistry.GetDefault(ConfigKeys.DataDir);
Directory.CreateDirectory(dataDir);
Directory.CreateDirectory(Path.Combine(dataDir, "logs"));

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
builder.Services.AddSingleton<ICookieRepository, CookieRepository>();
builder.Services.AddSingleton<IConfigRepository, ConfigRepository>();

// Application services.
builder.Services.AddSingleton<UserService>();
builder.Services.AddSingleton<CookiePoolService>();
builder.Services.AddSingleton(Log.Logger);

// Storage + proxy + downloader pipeline.
builder.Services.AddSingleton(new StorageService(dataDir));
builder.Services.AddSingleton<IProxyService>(sp => new ProxyService(
    sp.GetRequiredService<IConfigRepository>(),
    sp.GetRequiredService<ILogger>(),
    dataDir,
    builder.Configuration));
builder.Services.AddSingleton(sp => new YtdlpService(
    sp.GetRequiredService<IProxyService>(),
    sp.GetRequiredService<IConfigRepository>(),
    sp.GetRequiredService<ILogger>(),
    dataDir));
builder.Services.AddSingleton<StealthBrowserService>();
// Cookie refresh strategies: site-specific checks (YouTube session rejection)
// are picked per group at refresh time; the default applies to other sites.
builder.Services.AddSingleton<ICookieRefreshStrategy, DefaultCookieRefreshStrategy>();
builder.Services.AddSingleton<ICookieRefreshStrategy, YouTubeCookieRefreshStrategy>();
// Downloader plugins are self-discovered via reflection; no manual registration.
builder.Services.AddSingleton(sp => DownloaderFactory.Create(sp));
builder.Services.AddSingleton<ITaskExecutor>(sp => new DownloaderTaskExecutor(
    sp.GetRequiredService<DownloaderFactory>(),
    sp.GetRequiredService<IProxyService>(),
    sp.GetRequiredService<ILogger>(),
    (parent, child, referrer) =>
    {
        var taskService = sp.GetRequiredService<TaskService>();
        return taskService.Submit(parent.OwnerUserId, child.Url, parentId: parent.Id, title: child.Title, referrer: referrer);
    }));
builder.Services.AddSingleton<TaskService>();

// Background periodic services.
builder.Services.AddHostedService<StartupTasksHostedService>();
builder.Services.AddHostedService<DependencyUpgradeHostedService>();
builder.Services.AddHostedService<CookieRefreshHostedService>();
builder.Services.AddHostedService<CleanupHostedService>();

// Server-side session cookie authentication.
var sessionHours = int.TryParse(builder.Configuration["Account:SessionHours"], out var sh) ? sh : 168;
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
    .AddPolicy("Admin", p => p.RequireRole(nameof(UserRole.Admin)));

// Production: response compression + HTTPS redirection.
builder.Services.AddResponseCompression(options => options.EnableForHttps = true);

var app = builder.Build();

app.UseResponseCompression();
if (!builder.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();

AuthApi.Map(app);
UsersApi.Map(app);
TasksApi.Map(app);
ShareApi.Map(app);
CookiesApi.Map(app);
SystemApi.Map(app);
ConfigApi.Map(app);

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
}
