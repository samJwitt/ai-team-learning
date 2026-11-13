using System.Text;
using HomelabInventory.Web.Data;
using HomelabInventory.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace HomelabInventory.Web.Services.Update;

/// <summary>
/// Outcome of a lifecycle request. <see cref="Errors"/> carries refusal
/// messages for operations that never started; once an operation exists its
/// status and log tell the whole story.
/// </summary>
public sealed record LifecycleResult(
    AppOperation? Operation,
    IReadOnlyList<string> Errors)
{
    public bool Started => Operation is not null;

    public static LifecycleResult Refused(params string[] errors) => new(null, errors);

    public static LifecycleResult Finished(AppOperation operation) => new(operation, []);
}

/// <summary>
/// Move and Remove for tracked apps, built on the SAME ports the update
/// pipeline uses (IGitSource/IImageBuilder/IDeployer) — deliberately not a
/// second deploy stack.
///
/// Move semantics (host and/or port in one operation): deploy the app at the
/// new target FIRST (fetch → build → transfer → restart → verify up) and only
/// tear down the old container once the new one is verified running. Any
/// failure before that verification leaves the old deployment untouched and
/// the TrackedApp record unchanged. A same-host port change needs no separate
/// teardown: the restart at the new port already replaces the one container.
///
/// Remove semantics: best-effort teardown (an already-gone host or container
/// is logged and skipped, never blocks the delete), then the TrackedApp row is
/// deleted. The AppOperation audit row has no FK, so it outlives the record.
/// </summary>
public sealed class AppLifecycleService(
    IServiceScopeFactory scopeFactory,
    IGitSource gitSource,
    IImageBuilder imageBuilder,
    IDeployer deployer,
    IConfiguration configuration,
    IHostEnvironment environment)
{
    private readonly SemaphoreSlim operationGate = new(1, 1);

    public async Task<LifecycleResult> MoveAsync(
        int appId,
        string? newHost,
        int? newPort,
        CancellationToken ct = default)
    {
        TrackedApp? app;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HomelabDbContext>();
            app = await db.TrackedApps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == appId, ct);
        }

        if (app is null)
        {
            return LifecycleResult.Refused($"No app with id {appId}.");
        }

        newHost = string.IsNullOrWhiteSpace(newHost) ? null : newHost.Trim();

        // Validation errors are collected and returned in full so the form can
        // show every problem at once.
        var errors = new List<string>();
        if (newHost is not null && !DeployToken.IsValid(newHost))
        {
            errors.Add($"'{newHost}' is not a valid host name or address.");
        }
        if (newPort is < 1 or > 65535)
        {
            errors.Add("Port must be between 1 and 65535.");
        }
        var hostChanged = newHost is not null && !string.Equals(newHost, app.DeployedOn, StringComparison.Ordinal);
        var portChanged = newPort is not null && newPort != app.Port;
        if (errors.Count == 0 && !hostChanged && !portChanged)
        {
            errors.Add("The move target equals the current deployment — nothing to do.");
        }

        // A move re-deploys the app at the new target through the update
        // machinery, so it needs the same metadata a plain update needs.
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
            errors.Add(
                $"'{app.Name}' cannot be moved: missing {string.Join(", ", missing)}. " +
                "Edit the app and fill them in first.");
        }

        if (errors.Count > 0)
        {
            return LifecycleResult.Refused([.. errors]);
        }

        if (!await operationGate.WaitAsync(TimeSpan.Zero, ct))
        {
            return LifecycleResult.Refused(
                "Another move or remove is already running. This first cut runs one " +
                "lifecycle operation at a time — try again when it finishes.");
        }

        try
        {
            return await ExecuteMoveAsync(app, newHost ?? app.DeployedOn!, newPort ?? app.Port, ct);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<LifecycleResult> RemoveAsync(int appId, CancellationToken ct = default)
    {
        TrackedApp? app;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HomelabDbContext>();
            app = await db.TrackedApps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == appId, ct);
        }

        if (app is null)
        {
            return LifecycleResult.Refused($"No app with id {appId}.");
        }

        if (!await operationGate.WaitAsync(TimeSpan.Zero, ct))
        {
            return LifecycleResult.Refused(
                "Another move or remove is already running. This first cut runs one " +
                "lifecycle operation at a time — try again when it finishes.");
        }

        try
        {
            return await ExecuteRemoveAsync(app, ct);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<LifecycleResult> ExecuteMoveAsync(
        TrackedApp app,
        string targetHost,
        int? targetPort,
        CancellationToken ct)
    {
        var log = new StringBuilder();
        void Step(string line) => log.AppendLine(line);

        var branch = string.IsNullOrWhiteSpace(app.Branch) ? "main" : app.Branch;
        var workDir = Path.Combine(WorkRootPath, $"app-{app.Id}");
        var oldHost = app.DeployedOn!;

        var opId = await CreateOperationAsync(app, AppOperationKind.Move, targetHost, targetPort,
            oldHost, app.Port,
            log.AppendLine($"Queued move of '{app.Name}' from {oldHost}:{app.Port?.ToString() ?? "no port"} " +
                           $"to {targetHost}:{targetPort?.ToString() ?? "no port"}")
                .ToString(), ct);

        try
        {
            await PersistStatusAsync(opId, AppOperationStatus.Deploying, null, log, ct);

            Step($"-- Fetching branch '{branch}' from {app.RepoUrl}");
            var sha = await gitSource.FetchLatestAsync(app.RepoUrl!, branch, workDir, Step, ct);
            var imageTag = $"{app.ContainerImage}:{sha}";
            await PersistStatusAsync(opId, AppOperationStatus.Deploying, imageTag, log, ct);

            Step($"-- Building {imageTag}");
            await imageBuilder.BuildAsync(app.ContainerImage!, sha, workDir, Step, ct);

            // Deploy at the NEW target before touching the old one. On a
            // same-host port change, RestartContainerAsync replaces the single
            // existing container in place — no separate teardown below.
            Step($"-- Transferring image to {targetHost}");
            await deployer.TransferImageAsync(targetHost, app.ContainerImage!, sha, Step, ct);

            Step($"-- Starting container '{UpdatePipelineService.ContainerName(app)}' on {targetHost}");
            await deployer.RestartContainerAsync(
                targetHost, UpdatePipelineService.ContainerName(app), imageTag, targetPort, Step, ct);

            Step($"-- Verifying new deployment on {targetHost}");
            if (!await deployer.IsContainerRunningAsync(
                    targetHost, UpdatePipelineService.ContainerName(app), Step, ct))
            {
                throw new UpdateStepException(
                    "deploy",
                    $"Container did not come up on {targetHost}; the previous deployment on {oldHost} is untouched.");
            }

            // New deployment verified up — now release the old target.
            if (!string.Equals(oldHost, targetHost, StringComparison.Ordinal))
            {
                await PersistStatusAsync(opId, AppOperationStatus.TearingDown, imageTag, log, ct);
                Step($"-- Removing old container '{UpdatePipelineService.ContainerName(app)}' from {oldHost}");
                // Deliberately NOT tolerant here: the app is up on the new host,
                // so a failed cleanup of the OLD host is a real (reported)
                // problem — the record must not silently claim a half-done move.
                await deployer.TeardownAsync(oldHost, UpdatePipelineService.ContainerName(app), Step, ct);
            }

            await ApplyMoveAsync(app.Id, targetHost, targetPort, log, ct);
            await FinishAsync(opId, AppOperationStatus.Succeeded, imageTag, log, ct);
        }
        catch (Exception ex)
        {
            // The TrackedApp record is left pointing at the old deployment:
            // that is the whole point of deploy-new-first. The operator's
            // existing app is exactly where they left it.
            Step($"-- FAILED: {ex.Message}");
            if (ex is UpdateStepException stepEx && !string.IsNullOrWhiteSpace(stepEx.Output))
            {
                Step(stepEx.Output.TrimEnd());
            }
            else
            {
                Step(ex.ToString());
            }
            await FinishAsync(opId, AppOperationStatus.Failed, null, log, ct);
        }

        return LifecycleResult.Finished(await LoadOperationAsync(opId, ct));
    }

    private async Task<LifecycleResult> ExecuteRemoveAsync(TrackedApp app, CancellationToken ct)
    {
        var log = new StringBuilder();
        void Step(string line) => log.AppendLine(line);

        var opId = await CreateOperationAsync(app, AppOperationKind.Remove, app.DeployedOn, app.Port,
            null, null,
            log.AppendLine($"Queued removal of '{app.Name}' from {app.DeployedOn ?? "no recorded host"}")
                .ToString(), ct);

        await PersistStatusAsync(opId, AppOperationStatus.TearingDown, null, log, ct);

        // Best-effort by contract: anything that goes wrong on the host side
        // (unreachable machine, already-dead container, junk host name) is
        // logged and skipped — the inventory record must remain deletable.
        if (string.IsNullOrWhiteSpace(app.DeployedOn))
        {
            Step("-- No deploy host recorded; skipping container teardown.");
        }
        else if (!DeployToken.IsValid(app.DeployedOn))
        {
            Step($"-- Deployed-on host '{app.DeployedOn}' is not a valid token; skipping container teardown.");
        }
        else
        {
            try
            {
                Step($"-- Removing container '{UpdatePipelineService.ContainerName(app)}' from {app.DeployedOn}");
                await deployer.TeardownAsync(
                    app.DeployedOn, UpdatePipelineService.ContainerName(app), Step, ct);
            }
            catch (Exception ex)
            {
                Step($"-- Teardown on {app.DeployedOn} failed ({ex.Message}); proceeding with record deletion.");
            }
        }

        await DeleteAppAsync(app.Id, log, ct);
        await FinishAsync(opId, AppOperationStatus.Succeeded, null, log, ct);

        return LifecycleResult.Finished(await LoadOperationAsync(opId, ct));
    }

    /// <summary>
    /// Point the registry at the new target in its own scope, after the new
    /// deployment verified up. This is the single write that makes a move
    /// visible to every other page.
    /// </summary>
    private async Task ApplyMoveAsync(
        int appId, string targetHost, int? targetPort, StringBuilder log, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HomelabDbContext>();
        var row = await db.TrackedApps.FindAsync([appId], ct);
        if (row is null)
        {
            throw new UpdateStepException("apply", $"App {appId} vanished during the move.");
        }
        row.DeployedOn = targetHost;
        row.Port = targetPort;
        await db.SaveChangesAsync(ct);
        log.AppendLine($"-- Inventory updated: {row.Name} now at {targetHost}:{targetPort?.ToString() ?? "no port"}.");
    }

    private async Task DeleteAppAsync(int appId, StringBuilder log, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HomelabDbContext>();
        var row = await db.TrackedApps.FindAsync([appId], ct);
        if (row is null)
        {
            throw new UpdateStepException("remove", $"App {appId} vanished during removal.");
        }
        db.TrackedApps.Remove(row);
        await db.SaveChangesAsync(ct);
        log.AppendLine($"-- Deleted inventory record for '{row.Name}'.");
    }

    private async Task<int> CreateOperationAsync(
        TrackedApp app,
        AppOperationKind kind,
        string? targetHost,
        int? targetPort,
        string? oldHost,
        int? oldPort,
        string firstLogLine,
        CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HomelabDbContext>();
        var operation = new AppOperation
        {
            TrackedAppId = app.Id,
            Kind = kind,
            Status = AppOperationStatus.Queued,
            StartedAtUtc = DateTime.UtcNow,
            TargetHost = targetHost,
            HostPort = targetPort ?? 0,
            OldHost = oldHost,
            OldPort = oldPort ?? 0,
            Log = UpdateRunLog.Cap(firstLogLine),
        };
        db.AppOperations.Add(operation);
        await db.SaveChangesAsync(ct);
        return operation.Id;
    }

    private async Task PersistStatusAsync(
        int opId,
        AppOperationStatus status,
        string? imageTag,
        StringBuilder log,
        CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HomelabDbContext>();
        var operation = await db.AppOperations.FindAsync([opId], ct);
        operation!.Status = status;
        operation.Log = UpdateRunLog.Cap(log.ToString());
        if (imageTag is not null)
        {
            operation.ImageTag = imageTag;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task FinishAsync(
        int opId,
        AppOperationStatus status,
        string? imageTag,
        StringBuilder log,
        CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HomelabDbContext>();
        var operation = await db.AppOperations.FindAsync([opId], ct);
        operation!.Status = status;
        operation.CompletedAtUtc = DateTime.UtcNow;
        operation.Log = UpdateRunLog.Cap(log.ToString());
        if (imageTag is not null)
        {
            operation.ImageTag = imageTag;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<AppOperation> LoadOperationAsync(int opId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HomelabDbContext>();
        return await db.AppOperations.AsNoTracking().FirstAsync(o => o.Id == opId, ct);
    }

    /// <summary>
    /// Same interrupted-work sweep as update runs: a process kill mid-Move
    /// must not leave an operation forever in Deploying/TearingDown.
    /// </summary>
    public static int MarkInterruptedOperationsFailed(HomelabDbContext db)
    {
        var interrupted = db.AppOperations
            .Where(o => o.Status != AppOperationStatus.Succeeded
                        && o.Status != AppOperationStatus.Failed)
            .ToList();

        foreach (var operation in interrupted)
        {
            operation.Status = AppOperationStatus.Failed;
            operation.CompletedAtUtc = DateTime.UtcNow;
            operation.Log = UpdateRunLog.Cap(
                operation.Log + "\n-- Interrupted: application stopped before this operation finished.\n");
        }

        if (interrupted.Count > 0)
        {
            db.SaveChanges();
        }

        return interrupted.Count;
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
}
