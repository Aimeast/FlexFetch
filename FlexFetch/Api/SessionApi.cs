using FlexFetch.Services.Session;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Api;

public sealed record SessionImportRequest(string? Text, string? Url);

/// <summary>
/// Admin session endpoints, segmented by site: /api/session/youtube/* (the
/// cookie import with the InnerTube probe gate, the refresh pipeline, the
/// canary, the PO token provider and the diagnostic matrix) and
/// /api/session/twitter/* (the auth_token import with the live identity
/// gate, the status and the manual re-check).
/// </summary>
public static class SessionApi
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/session").RequireAuthorization("Admin");

        // ---- YouTube session ----

        group.MapPost("/youtube/import", async (SessionImportRequest req, YouTubeSessionExportService export, ILogger log, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Text))
            {
                return Results.BadRequest(new { error = "Text is required" });
            }

            Uri? url = null;
            if (!string.IsNullOrWhiteSpace(req.Url) && !Uri.TryCreate(req.Url, UriKind.Absolute, out url))
            {
                return Results.BadRequest(new { error = "Url must be an absolute URL when given" });
            }

            try
            {
                var result = await export.ImportAsync(req.Text, url, ct);
                log.Information("Session import via API: ok={Ok} parsed={Parsed} imported={Imported}",
                    result.Ok, result.Parsed, result.ImportedToSnapshot);
                return result.Ok
                    ? Results.Ok(result)
                    : Results.BadRequest(result);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (OperationCanceledException)
            {
                // The browser gave up on the probe (typically a user retry
                // after a long, previously silent wait) - log it so the gap
                // is visible in the file log too.
                log.Information("Session import aborted by the client");
                throw;
            }
        });

        group.MapPost("/youtube/export", (YouTubeSessionExportService export, ILogger log) =>
        {
            if (export.IsRunning)
            {
                return Results.Conflict(new { error = "A session export is already running" });
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await export.RunExportAsync();
                }
                catch (Exception ex)
                {
                    log.Warning(ex, "Manual session export failed");
                }
            });
            return Results.Accepted((string?)null, new { exporting = true });
        });

        group.MapGet("/youtube/status", (YouTubeSessionStore snapshot, YouTubeSessionExportService export) => Results.Ok(new
        {
            exists = snapshot.Exists,
            ageMinutes = snapshot.Age?.TotalMinutes,
            meta = snapshot.ReadMeta(),
            cookieCount = snapshot.ReadCookies().Count,
            storage = "encrypted v1",
            export.IsRunning,
            export.CurrentStage,
            export.CurrentDetail,
            export.LastRunAt,
            lastResult = export.LastResult.ToString(),
            export.LastError,
        }));

        group.MapGet("/youtube/canary", (HostedServices.YouTubeSessionMaintenanceHostedService maintenance, YouTubeSessionStore snapshot) => Results.Ok(new
        {
            exists = snapshot.Exists,
            lastCheckAt = maintenance.LastCheckAt,
            lastHealth = maintenance.LastHealth.ToString(),
            maintenance.LastDetail,
            maintenance.ConsecutiveFailures,
        }));

        group.MapGet("/youtube/pot-status", (PotProviderService pot) => Results.Ok(new
        {
            mode = "bgutil script via deno (no server process)",
            denoInstalled = pot.IsDenoInstalled,
            serverInstalled = pot.IsServerInstalled,
            pluginInstalled = pot.IsPluginInstalled,
            scriptPath = pot.ScriptPath,
            pot.LastError,
        }));

        // Runs a new matrix; the report is stored and also returned by GET.
        group.MapPost("/youtube/diagnose", async (YouTubeSessionDiagnosisService diagnosis, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await diagnosis.RunAsync(ct));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        // The last stored matrix report (null when none was run yet), so the
        // page shows the previous result by default after a refresh.
        group.MapGet("/youtube/diagnose", (YouTubeSessionDiagnosisService diagnosis) =>
            Results.Ok(new { last = diagnosis.Last, running = diagnosis.IsRunning }));

        // ---- X (Twitter) session ----

        group.MapPost("/twitter/import", async (SessionImportRequest req, TwitterSessionService twitter, ILogger log, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Text))
            {
                return Results.BadRequest(new { error = "Text is required" });
            }

            try
            {
                var result = await twitter.ImportAsync(req.Text, ct);
                log.Information("Twitter session import via API: ok={Ok} stored={Stored} account={Account}",
                    result.Ok, result.Stored, result.Account);
                return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        group.MapGet("/twitter/status", (TwitterSessionStore store) => Results.Ok(new
        {
            exists = store.Exists,
            meta = store.ReadMeta(),
            cookieCount = store.ReadCookies().Count,
            storage = "encrypted v1",
        }));

        group.MapPost("/twitter/check", async (TwitterSessionService twitter, CancellationToken ct) =>
        {
            try
            {
                var health = await twitter.CheckAsync(ct);
                return Results.Ok(new { health = health.ToString() });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        group.MapDelete("/twitter", (TwitterSessionStore store, ILogger log) =>
        {
            store.Delete();
            log.Information("Twitter session removed via API");
            return Results.Ok(new { removed = true });
        });
    }
}
