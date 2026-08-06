using FlexFetch.Api;
using FlexFetch.Config;
using FlexFetch.Data;
using FlexFetch.Domain;
using FlexFetch.Services;
using FlexFetch.Services.Downloaders;
using Microsoft.AspNetCore.Authentication.Cookies;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog: structured logging to console and rolling files.
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        path: "logs/flexfetch-.log",
        rollingInterval: RollingInterval.Day,
        fileSizeLimitBytes: 100 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

builder.Host.UseSerilog();

// Data directory (independent of program directory; overridable via config).
var dataDir = builder.Configuration["Data:Dir"] ?? ConfigRegistry.GetDefault(ConfigKeys.DataDir);
Directory.CreateDirectory(dataDir);

// Data layer: one shared LiteDB store instance.
builder.Services.AddSingleton(new LiteDbStore(Path.Combine(dataDir, "flexfetch.db")));
builder.Services.AddSingleton<IUserRepository, UserRepository>();
builder.Services.AddSingleton<ITaskRepository, TaskRepository>();
builder.Services.AddSingleton<IShareRepository, ShareRepository>();
builder.Services.AddSingleton<ICookieRepository, CookieRepository>();
builder.Services.AddSingleton<IConfigRepository, ConfigRepository>();

// Application services.
builder.Services.AddSingleton<UserService>();
builder.Services.AddSingleton(Log.Logger);

// Storage + proxy + downloader pipeline.
builder.Services.AddSingleton(new StorageService(dataDir));
builder.Services.AddSingleton<IProxyService, ProxyService>();
builder.Services.AddSingleton<IDownloader, GenericFileDownloader>();
builder.Services.AddSingleton<DownloaderFactory>();
builder.Services.AddSingleton<ITaskExecutor, DownloaderTaskExecutor>();
builder.Services.AddSingleton<TaskService>();

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

var app = builder.Build();

// Bootstrap: create the initial admin when none exists and a password is configured.
app.Services.GetRequiredService<UserService>()
    .EnsureInitialAdmin(builder.Configuration["Admin:InitialPassword"]);

app.UseAuthentication();
app.UseAuthorization();

AuthApi.Map(app);
UsersApi.Map(app);

app.MapGet("/", () => "FlexFetch is running.");

app.Run();

// Expose the generated Program class for integration tests (WebApplicationFactory).
public partial class Program { }
