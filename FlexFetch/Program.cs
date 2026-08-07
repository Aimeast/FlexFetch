using FlexFetch.Api;
using FlexFetch.Config;
using FlexFetch.Data;
using FlexFetch.Entities;
using FlexFetch.Enums;
using FlexFetch.HostedServices;
using FlexFetch.Services;
using FlexFetch.Services.Downloaders;
using FlexFetch.Services.Routing;
using FlexFetch.Services.Tasks;
using Microsoft.AspNetCore.Authentication.Cookies;
using Serilog;
using ILogger = Serilog.ILogger;

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
builder.Services.AddHostedService<ComponentUpgradeHostedService>();
builder.Services.AddHostedService<CookieRefreshHostedService>();
builder.Services.AddHostedService<CleanupHostedService>();
builder.Services.AddHostedService<InactiveUserCleanupHostedService>();

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

// Bootstrap: create the initial admin when none exists and a password is configured.
app.Services.GetRequiredService<UserService>()
    .EnsureInitialAdmin(builder.Configuration["Admin:InitialPassword"]);

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
app.UseStaticFiles();

app.Run();

// Expose the generated Program class for integration tests (WebApplicationFactory).
public partial class Program
{
    /// <summary>Process start time (UTC), surfaced by the system info API.</summary>
    public static readonly DateTime StartedAt = DateTime.UtcNow;
}
