using FlexFetch.Api;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Serilog;

namespace FlexFetch.Startup;

/// <summary>Request pipeline, endpoint mapping and static files.</summary>
public static class PipelineConfig
{
    public static void UseFlexFetchPipeline(this WebApplication app)
    {
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
                if (app.Environment.IsDevelopment())
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
    }
}
