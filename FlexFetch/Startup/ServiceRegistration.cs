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
using Serilog;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Startup;

/// <summary>Dependency registration for the data layer, services and auth.</summary>
public static class ServiceRegistration
{
    public static void AddFlexFetchServices(this IServiceCollection services, string dataDir, IConfiguration config)
    {
        // Data layer: one shared LiteDB store instance.
        services.AddSingleton(new LiteDbStore(Path.Combine(dataDir, "flexfetch.db")));
        services.AddSingleton<IUserRepository, UserRepository>();
        services.AddSingleton<ITaskRepository, TaskRepository>();
        services.AddSingleton<IShareRepository, ShareRepository>();
        services.AddSingleton<IGuestRepository, GuestRepository>();

        // Application services.
        services.AddSingleton<UserService>();
        services.AddSingleton<GuestSessionService>();
        services.AddSingleton(Log.Logger);

        // Storage + proxy + downloader pipeline.
        services.AddSingleton(new StorageService(dataDir));
        services.AddSingleton<IProxyService>(sp => new ProxyService(
            config,
            sp.GetRequiredService<ILogger>(),
            dataDir));
        services.AddSingleton(sp => new YtdlpService(
            sp.GetRequiredService<IProxyService>(),
            sp.GetRequiredService<ILogger>(),
            dataDir));

        // Session maintenance: the Firefox session source, the snapshot store (the
        // single source of truth for the YouTube login session), the InnerTube probe
        // and the export pipeline (the only snapshot writer).
        services.AddSingleton<SessionSnapshotService>();
        services.AddSingleton<FirefoxBrowserService>();
        services.AddSingleton<SessionProbeService>();
        services.AddSingleton<SessionExportService>();
        services.AddSingleton<SessionDiagnosisService>();
        services.AddSingleton<PotProviderService>();

        // Downloader plugins are self-discovered via reflection; no manual registration.
        services.AddSingleton(sp => DownloaderFactory.Create(sp));
        services.AddSingleton<ITaskExecutor>(sp => new DownloaderTaskExecutor(
            sp.GetRequiredService<DownloaderFactory>(),
            sp.GetRequiredService<IProxyService>(),
            sp.GetRequiredService<ILogger>(),
            (parent, child, referrer) =>
            {
                var taskService = sp.GetRequiredService<TaskService>();
                return taskService.Submit(parent.OwnerUserId, child.Url, downloaderType: child.DownloaderType,
                    parentId: parent.Id, title: child.Title, referrer: referrer);
            }));
        services.AddSingleton<TaskService>();

        // Background periodic services. Session maintenance and the startup task
        // service are registered as concrete singletons first so other services can
        // inject them (the session status API reads maintenance; the pot supervisor
        // and maintenance await the startup component plan).
        services.AddSingleton<SessionMaintenanceHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<SessionMaintenanceHostedService>());
        services.AddSingleton<StartupTasksHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<StartupTasksHostedService>());
        services.AddHostedService<DependencyUpgradeHostedService>();
        services.AddHostedService<PotSupervisorHostedService>();
        services.AddHostedService<CleanupHostedService>();

        // The auth cookie is protected by the Data Protection key ring; keep it in
        // the data dir (volume-backed) so cookies survive container recreation -
        // keys left in the container layer die with the image update and force
        // every logged-in browser to re-login.
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));

        // Server-side session cookie authentication.
        var sessionHours = int.Parse(ConfigRegistry.From(config, ConfigKeys.SessionHours));
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
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
        services.AddAuthorizationBuilder()
            .AddPolicy("Admin", p => p.RequireRole(nameof(UserRole.Admin)))
            // App functionality (task API): authenticated users, or anonymous
            // visitors when account.allowAnonymous is enabled (shared guest pool).
            .AddPolicy("AppAccess", p => p.AddRequirements(new AppAccessRequirement()));
        services.AddSingleton<IAuthorizationHandler, AppAccessHandler>();

        // Production: response compression + HTTPS redirection.
        services.AddResponseCompression(options => options.EnableForHttps = true);
    }
}
