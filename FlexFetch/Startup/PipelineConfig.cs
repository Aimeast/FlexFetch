using FlexFetch.Api;
using FlexFetch.Config;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Serilog;

namespace FlexFetch.Startup;

/// <summary>Request pipeline, endpoint mapping and static files.</summary>
public static class PipelineConfig
{
    public static void UseFlexFetchPipeline(this WebApplication app)
    {
        // Virtual-path hosting: strip the configured URL prefix (like
        // "/flexfetch") from incoming requests so routing and static files
        // see the plain path. Requests without the prefix keep working, so
        // the app serves at the root and under the prefix at the same time;
        // the document-relative UI references make both forms correct. Read
        // once here: the middleware chain is fixed at startup, so a config
        // change needs a restart.
        var pathBase = NormalizePathBase(ConfigRegistry.From(app.Configuration, ConfigKeys.PathBase));
        if (pathBase.Length > 0)
        {
            // Before the prefix is stripped: a bare ".../flexfetch" (no
            // trailing slash) must redirect to ".../flexfetch/", because
            // document-relative references resolve against the nearest
            // directory of the document URL.
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.Value == pathBase)
                {
                    context.Response.Redirect(pathBase + "/" + context.Request.QueryString);
                    return;
                }

                await next(context);
            });
            app.UsePathBase(pathBase);
        }

        app.UseResponseCompression();

        // Baseline response hardening for every surface (API and static
        // pages). Inline scripts keep a strict CSP impractical, but these
        // stop MIME sniffing and framing.
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Referrer-Policy"] = "same-origin";
            await next(context);
        });

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
                        // PathBase carries the configured virtual-path prefix
                        // (UsePathBase moved it out of Path): the redirect
                        // target must keep the prefix or a sub-path hosting
                        // would bounce browsers to a dead root URL.
                        context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
                        context.Response.Headers.Location =
                            $"https://{context.Request.Host.Host}:{new Uri(httpsAddress).Port}{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}";
                        return;
                    }
                }

                await next(context);
            });
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

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

    /// <summary>Tolerant reader for web.pathBase: accepts "flexfetch",
    /// "/flexfetch" and "/flexfetch/" and returns the canonical form with a
    /// leading slash and no trailing slash (empty for root hosting).</summary>
    private static string NormalizePathBase(string? value)
    {
        var segments = (value ?? string.Empty).Trim().Trim('/');
        return segments.Length == 0 ? string.Empty : "/" + segments;
    }
}
