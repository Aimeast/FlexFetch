using FlexFetch.Services.Session;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Api;

public sealed record SessionImportRequest(string? Text, string? Url);

/// <summary>
/// Admin session endpoints: import a human-exported YouTube cookie jar,
/// observe the export pipeline / canary / PO token provider, trigger a
/// manual export, and run the diagnostic matrix (client posture x
/// anonymous/session) that every session problem attribution starts from.
/// </summary>
public static class SessionApi
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/session").RequireAuthorization("Admin");

        group.MapPost("/import", async (SessionImportRequest req, SessionExportService export, ILogger log, CancellationToken ct) =>
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
        });

        group.MapPost("/export", (SessionExportService export, ILogger log) =>
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

        group.MapGet("/export/status", (SessionSnapshotService snapshot, SessionExportService export) => Results.Ok(new
        {
            export.IsRunning,
            export.CurrentStage,
            export.LastRunAt,
            lastResult = export.LastResult.ToString(),
            export.LastError,
            snapshotExists = snapshot.Exists,
            snapshotAge = snapshot.Age?.TotalMinutes,
            meta = snapshot.ReadMeta(),
        }));

        group.MapGet("/canary", (HostedServices.SessionMaintenanceHostedService maintenance, SessionSnapshotService snapshot) => Results.Ok(new
        {
            snapshotExists = snapshot.Exists,
            lastCheckAt = maintenance.LastCheckAt,
            lastHealth = maintenance.LastHealth.ToString(),
            maintenance.LastDetail,
            maintenance.ConsecutiveFailures,
        }));

        group.MapGet("/pot-status", (PotProviderService pot) => Results.Ok(new
        {
            mode = "bgutil script via deno (no server process)",
            denoInstalled = pot.IsDenoInstalled,
            serverInstalled = pot.IsServerInstalled,
            pluginInstalled = pot.IsPluginInstalled,
            scriptPath = pot.ScriptPath,
            pot.LastError,
        }));

        // Runs a new matrix; the report is stored and also returned by GET.
        group.MapPost("/diagnose", async (SessionDiagnosisService diagnosis, CancellationToken ct) =>
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
        group.MapGet("/diagnose", (SessionDiagnosisService diagnosis) =>
            Results.Ok(new { last = diagnosis.Last, running = diagnosis.IsRunning }));
    }
}
