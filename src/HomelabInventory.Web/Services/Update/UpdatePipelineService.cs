using System.Text;
using HomelabInventory.Web.Data;
using HomelabInventory.Web.Models;
using HomelabInventory.Web.Services.Apps;
using Microsoft.EntityFrameworkCore;

namespace HomelabInventory.Web.Services.Update;

/// <summary>
/// Outcome of a run request. <see cref="Errors"/> carries refusal messages for
/// runs that never started (unknown app, missing metadata, busy pipeline);
/// once a run exists, its status and log tell the whole story.
/// </summary>
public sealed record UpdateRunResult(
    UpdateRun? Run,
    IReadOnlyList<string> Errors)
{
    public bool Started => Run is not null;

    public static UpdateRunResult Refused(params string[] errors) => new(null, errors);

    public static UpdateRunResult Finished(UpdateRun run) => new(run, []);
}

/// <summary>
/// Runs the three-stage update pipeline (fetch → build → push/restart) for a
/// tracked app, persisting an <see cref="UpdateRun"/> whose status the detail
/// page polls for live progress. The first cut is synchronous: one pipeline
/// runs at a time per app instance, and a concurrent request is refused
/// instead of queued.
/// </summary>
public sealed class UpdatePipelineService(
    IServiceScopeFactory scopeFactory,
    IGitSource gitSource,
    IImageBuilder imageBuilder,
    IDeployer deployer,
    IConfiguration configuration,
    IHostEnvironment environment)
{
    private readonly SemaphoreSlim pipelineGate = new(1, 1);

    public async Task<UpdateRunResult> RunAsync(int appId, CancellationToken ct = default)
    {
        TrackedApp? app;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HomelabDbContext>();
            app = await db.TrackedApps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == appId, ct);
        }

        if (app is null)
        {
            return UpdateRunResult.Refused($"No app with id {appId}.");
        }

        // Refusal before any work (acceptance criterion 3): the run must never
        // reach Building without the metadata the stages need.
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(app.RepoUrl))
        {
            missing.Add("Repository URL");
        }
        if (string.IsNullOrWhiteSpace(app.ContainerImage))
        {
            missing.Add("Container image");
        }
        if (string.IsNullOrWhiteSpace(app.DeployedOn))
        {
            missing.Add("Deployed on");
        }
        if (missing.Count > 0)
        {
            return UpdateRunResult.Refused(
                $"'{app.Name}' cannot run an update: missing {string.Join(", ", missing)}. " +
                "Edit the app and fill them in first.");
        }

        if (!await pipelineGate.WaitAsync(TimeSpan.Zero, ct))
        {
            return UpdateRunResult.Refused(
                "Another update is already running. This first cut runs one update at a " +
                "time — try again when it finishes.");
        }

        try
        {
            return await ExecuteAsync(app, ct);
        }
        finally
        {
            pipelineGate.Release();
        }
    }

    private async Task<UpdateRunResult> ExecuteAsync(TrackedApp app, CancellationToken ct)
    {
        var log = new StringBuilder();
        void Step(string line) => log.AppendLine(line);

        var branch = string.IsNullOrWhiteSpace(app.Branch) ? "main" : app.Branch;
        var workDir = Path.Combine(WorkRootPath, $"app-{app.Id}");

        var runId = await CreateRunAsync(app, branch, log, ct);

        try
        {
            await PersistStatusAsync(runId, UpdateRunStatus.Building, null, log, ct);

            Step($"-- Fetching branch '{branch}' from {app.RepoUrl}");
            var sha = await gitSource.FetchLatestAsync(app.RepoUrl!, branch, workDir, Step, ct);
            var imageTag = $"{app.ContainerImage}:{sha}";
            await PersistStatusAsync(runId, UpdateRunStatus.Building, imageTag, log, ct);

            Step($"-- Building {imageTag}");
            await imageBuilder.BuildAsync(app.ContainerImage!, sha, workDir, Step, ct);

            await PersistStatusAsync(runId, UpdateRunStatus.Pushing, imageTag, log, ct);
            Step($"-- Pushing image to {app.DeployedOn}");
            await deployer.TransferImageAsync(app.DeployedOn!, app.ContainerImage!, sha, Step, ct);

            await PersistStatusAsync(runId, UpdateRunStatus.Deploying, imageTag, log, ct);
            Step($"-- Restarting container '{ContainerName(app)}' on {app.DeployedOn}");
            await deployer.RestartContainerAsync(
                app.DeployedOn!, ContainerName(app), imageTag, app.Port, Step, ct);

            await FinishAsync(runId, UpdateRunStatus.Succeeded, log, ct);
        }
        catch (Exception ex)
        {
            // Any failing stage ends the run Failed with that stage's output in
            // the log; the tracked app row itself is untouched (criterion 4).
            Step($"-- FAILED: {ex.Message}");
            if (ex is UpdateStepException stepEx)
            {
                if (!string.IsNullOrWhiteSpace(stepEx.Output))
                {
                    Step(stepEx.Output.TrimEnd());
                }
            }
            else
            {
                Step(ex.ToString());
            }
            await FinishAsync(runId, UpdateRunStatus.Failed, log, ct);
        }

        await using var finalScope = scopeFactory.CreateAsyncScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<HomelabDbContext>();
        var done = await finalDb.UpdateRuns.AsNoTracking().FirstAsync(r => r.Id == runId, ct);
        return UpdateRunResult.Finished(done);
    }

    private async Task<int> CreateRunAsync(
        TrackedApp app, string branch, StringBuilder log, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HomelabDbContext>();
        var run = new UpdateRun
        {
            TrackedAppId = app.Id,
            Status = UpdateRunStatus.Queued,
            StartedAtUtc = DateTime.UtcNow,
            Log = log.AppendLine($"Queued update of '{app.Name}' from {app.RepoUrl} (branch {branch})")
                .ToString(),
        };
        db.UpdateRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run.Id;
    }

    private async Task PersistStatusAsync(
        int runId,
        UpdateRunStatus status,
        string? imageTag,
        StringBuilder log,
        CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HomelabDbContext>();
        var run = await db.UpdateRuns.FindAsync([runId], ct);
        run!.Status = status;
        run.Log = log.ToString();
        if (imageTag is not null)
        {
            run.ImageTag = imageTag;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task FinishAsync(
        int runId,
        UpdateRunStatus status,
        StringBuilder log,
        CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HomelabDbContext>();
        var run = await db.UpdateRuns.FindAsync([runId], ct);
        run!.Status = status;
        run.CompletedAtUtc = DateTime.UtcNow;
        run.Log = log.ToString();
        await db.SaveChangesAsync(ct);
    }

    private string WorkRootPath
    {
        get
        {
            var configured = configuration["Update:WorkDir"];
            if (string.IsNullOrWhiteSpace(configured))
            {
                return Path.Combine(environment.ContentRootPath, "App_Data", "update-work");
            }
            return Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(environment.ContentRootPath, configured);
        }
    }

    /// <summary>
    /// Deterministic container name derived from the app name so restarts are
    /// idempotent (hl-grafana, hl-home-assistant, ...).
    /// </summary>
    public static string ContainerName(TrackedApp app)
    {
        var slug = app.Name.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var name = "hl-" + new string(slug);
        while (name.Contains("--", StringComparison.Ordinal))
        {
            name = name.Replace("--", "-", StringComparison.Ordinal);
        }
        return name.Trim('-');
    }
}
