using HomelabInventory.Web.Data;
using HomelabInventory.Web.Models;
using HomelabInventory.Web.Services.Apps;
using HomelabInventory.Web.Services.Update;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HomelabInventory.Tests.Services.Update;

/// <summary>
/// Hermetic integration tests for the app lifecycle operations (Move / Remove):
/// the real app hosted via WebApplicationFactory against a temp SQLite file,
/// with the pipeline ports replaced by the shared fakes — no network, no
/// docker, no SSH. Fake hosts use RFC 5737 TEST-NET addresses (192.0.2.x).
/// </summary>
public sealed class AppLifecycleTests : IDisposable
{
    private const string OldHost = "192.0.2.10";
    private const string NewHost = "192.0.2.20";

    private readonly string dbPath =
        Path.Combine(Path.GetTempPath(), $"homelab-lifecycle-{Guid.NewGuid():N}.db");

    private readonly FakePipelineRig rig = new();

    public void Dispose()
    {
        foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(dbPath) + "*"))
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    private UpdatePipelineFactory CreateFactory() => new(dbPath, rig);

    private static async Task<TrackedApp> CreateAppAsync(
        UpdatePipelineFactory factory,
        string name = "Grafana",
        string? deployedOn = OldHost,
        int? port = 3000)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TrackedAppService>();
        var app = new TrackedApp
        {
            Name = name,
            HostnameOrIp = "192.0.2.50",
            RepoUrl = "https://github.com/samJwitt/grafana-docker.git",
            Branch = "main",
            ContainerImage = "grafana",
            DeployedOn = deployedOn,
            Port = port,
        };

        var result = await service.CreateAsync(app);
        Assert.True(result.Success, string.Join("; ", result.Errors));
        return result.App!;
    }

    private Task<LifecycleResult> MoveAsync(UpdatePipelineFactory f, int id, string? host, int? port) =>
        f.Services.GetRequiredService<AppLifecycleService>().MoveAsync(id, host, port);

    private Task<LifecycleResult> RemoveAsync(UpdatePipelineFactory f, int id) =>
        f.Services.GetRequiredService<AppLifecycleService>().RemoveAsync(id);

    private async Task<TrackedApp?> ReloadAppAsync(UpdatePipelineFactory f, int id)
    {
        using var scope = f.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TrackedAppService>().GetAsync(id);
    }

    private async Task<List<AppOperation>> OpsAsync(UpdatePipelineFactory f, int id)
    {
        using var scope = f.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TrackedAppService>().ListOperationsAsync(id);
    }

    // ---- Move: happy paths -------------------------------------------------

    [Fact]
    public async Task MoveCrossHost_DeploysNewThenTearsDownOld_AndUpdatesRecord()
    {
        await using var factory = CreateFactory();
        var app = await CreateAppAsync(factory);
        var container = UpdatePipelineService.ContainerName(app);

        var result = await MoveAsync(factory, app.Id, NewHost, 3100);

        Assert.True(result.Started);
        Assert.Equal(AppOperationStatus.Succeeded, result.Operation!.Status);

        // New target deployed BEFORE the old one was touched.
        Assert.Equal(((string, string, string)?)(NewHost, "grafana", rig.Sha),
            rig.Deployer.Transferred);
        Assert.Equal(((string, string, string, int?)?)
            (NewHost, container, $"grafana:{rig.Sha}", 3100), rig.Deployer.Restarted);
        Assert.Equal([(OldHost, container)], rig.Deployer.Teardowns);
        Assert.Equal(["fetch", "build", "transfer", "restart", "verify", "teardown"], rig.Calls);

        // Record points at the new target.
        var reloaded = await ReloadAppAsync(factory, app.Id);
        Assert.Equal(NewHost, reloaded!.DeployedOn);
        Assert.Equal(3100, reloaded.Port);

        // Audit trail: operation row carries both targets and the run log.
        var ops = await OpsAsync(factory, app.Id);
        var op = Assert.Single(ops);
        Assert.Equal(AppOperationKind.Move, op.Kind);
        Assert.Equal(NewHost, op.TargetHost);
        Assert.Equal(3100, op.HostPort);
        Assert.Equal(OldHost, op.OldHost);
        Assert.Equal(3000, op.OldPort);
        Assert.Contains("Verifying new deployment", op.Log);
        Assert.Contains($"Removing old container '{container}' from {OldHost}", op.Log);
    }

    [Fact]
    public async Task MoveSameHostNewPort_ReplacesContainerInPlace_NoTeardown()
    {
        await using var factory = CreateFactory();
        var app = await CreateAppAsync(factory);

        var result = await MoveAsync(factory, app.Id, OldHost, 3101);

        Assert.Equal(AppOperationStatus.Succeeded, result.Operation!.Status);
        Assert.Empty(rig.Deployer.Teardowns); // in-place restart owns the name
        Assert.DoesNotContain("teardown", rig.Calls);
        Assert.Equal(3101, rig.Deployer.Restarted!.Value.Port);

        var reloaded = await ReloadAppAsync(factory, app.Id);
        Assert.Equal(OldHost, reloaded!.DeployedOn);
        Assert.Equal(3101, reloaded.Port);
    }

    // ---- Move: failure keeps the old deployment intact ----------------------

    [Theory]
    [InlineData("fetch")]
    [InlineData("build")]
    [InlineData("transfer")]
    [InlineData("restart")]
    public async Task MoveWhenStageFails_LeavesOldDeploymentAndRecordUntouched(string failAt)
    {
        await using var factory = CreateFactory();
        var app = await CreateAppAsync(factory);
        rig.FailAt = failAt;

        var result = await MoveAsync(factory, app.Id, NewHost, 3100);

        Assert.Equal(AppOperationStatus.Failed, result.Operation!.Status);
        // The old container is NEVER torn down when the new deploy fails.
        Assert.Empty(rig.Deployer.Teardowns);

        var reloaded = await ReloadAppAsync(factory, app.Id);
        Assert.Equal(OldHost, reloaded!.DeployedOn);
        Assert.Equal(3000, reloaded.Port);

        // Failure is reported with the stage's command output for triage.
        Assert.Contains($"fake-{failAt}-output", result.Operation.Log);
    }

    [Fact]
    public async Task MoveWhenNewContainerDoesNotComeUp_FailsWithoutTeardown()
    {
        await using var factory = CreateFactory();
        var app = await CreateAppAsync(factory);
        rig.Deployer.ContainerRunning = false;

        var result = await MoveAsync(factory, app.Id, NewHost, 3100);

        Assert.Equal(AppOperationStatus.Failed, result.Operation!.Status);
        Assert.Empty(rig.Deployer.Teardowns);
        Assert.Contains("did not come up", result.Operation.Log);
        Assert.Contains(OldHost, result.Operation.Log); // names the untouched old target

        var reloaded = await ReloadAppAsync(factory, app.Id);
        Assert.Equal(OldHost, reloaded!.DeployedOn);
    }

    [Fact]
    public async Task MoveWhenOldTeardownFails_ReportedFailedButNewDeploymentStands()
    {
        await using var factory = CreateFactory();
        var app = await CreateAppAsync(factory);
        rig.FailAt = "teardown"; // fails only at the old-host cleanup step

        var result = await MoveAsync(factory, app.Id, NewHost, 3100);

        // Deliberate contract: cleanup of the old host is a real step — if it
        // fails the move is NOT marked successful and the record does not
        // silently claim a half-done migration.
        Assert.Equal(AppOperationStatus.Failed, result.Operation!.Status);
        Assert.Contains("fake-teardown-output", result.Operation.Log);
        var reloaded = await ReloadAppAsync(factory, app.Id);
        Assert.Equal(OldHost, reloaded!.DeployedOn);
    }

    // ---- Move: refusal / validation (nothing spawns, nothing written) -------

    [Fact]
    public async Task MoveToTargetEqualToCurrent_IsRefused()
    {
        await using var factory = CreateFactory();
        var app = await CreateAppAsync(factory);

        var result = await MoveAsync(factory, app.Id, OldHost, 3000);

        Assert.False(result.Started);
        Assert.Contains(result.Errors, e => e.Contains("nothing to do"));
        Assert.Empty(rig.Calls);
    }

    [Fact]
    public async Task MoveWithShellInjectionHost_IsRefusedBeforeAnythingRuns()
    {
        await using var factory = CreateFactory();
        var app = await CreateAppAsync(factory);

        var result = await MoveAsync(factory, app.Id, "192.0.2.20; rm -rf /", 3100);

        Assert.False(result.Started);
        Assert.Contains(result.Errors, e => e.Contains("not a valid host"));
        Assert.Empty(rig.Calls);
        Assert.Empty(await OpsAsync(factory, app.Id));
    }

    [Fact]
    public async Task MoveWithOutOfRangePort_IsRefused()
    {
        await using var factory = CreateFactory();
        var app = await CreateAppAsync(factory);

        var result = await MoveAsync(factory, app.Id, NewHost, 70000);

        Assert.False(result.Started);
        Assert.Contains(result.Errors, e => e.Contains("Port must be between"));
        Assert.Empty(rig.Calls);
    }

    [Fact]
    public async Task MoveWhenAppMissingMetadata_IsRefusedWithPointerToEdit()
    {
        await using var factory = CreateFactory();
        var app = await CreateAppAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HomelabDbContext>();
            var row = await db.TrackedApps.FindAsync(app.Id);
            row!.RepoUrl = "";
            row.DeployedOn = null;
            await db.SaveChangesAsync();
        }

        var result = await MoveAsync(factory, app.Id, NewHost, 3100);

        Assert.False(result.Started);
        Assert.Contains(result.Errors, e => e.Contains("Repository URL") && e.Contains("Deployed on"));
        Assert.Empty(rig.Calls);
    }

    [Fact]
    public async Task MoveUnknownApp_IsRefused()
    {
        await using var factory = CreateFactory();

        var result = await MoveAsync(factory, 999, NewHost, 3100);

        Assert.False(result.Started);
        Assert.Empty(rig.Calls);
    }

    // ---- Remove -------------------------------------------------------------

    [Fact]
    public async Task RemoveTearsDownContainerThenDeletesRecord_LeavingAuditRow()
    {
        await using var factory = CreateFactory();
        var app = await CreateAppAsync(factory);
        var container = UpdatePipelineService.ContainerName(app);

        var result = await RemoveAsync(factory, app.Id);

        Assert.Equal(AppOperationStatus.Succeeded, result.Operation!.Status);
        Assert.Equal([(OldHost, container)], rig.Deployer.Teardowns);
        Assert.Equal(["teardown"], rig.Calls);

        // The inventory record is gone...
        Assert.Null(await ReloadAppAsync(factory, app.Id));

        // ...and the audit row outlives it (no FK by design).
        var ops = await OpsAsync(factory, app.Id);
        var op = Assert.Single(ops);
        Assert.Equal(AppOperationKind.Remove, op.Kind);
        Assert.Equal(OldHost, op.TargetHost);
        Assert.Contains("Deleted inventory record", op.Log);

        // The list page no longer offers the app.
        var html = await factory.CreateClient().GetStringAsync("/apps");
        Assert.DoesNotContain("Grafana", html);
    }

    [Fact]
    public async Task RemoveWhenContainerAlreadyGone_StillSucceeds()
    {
        await using var factory = CreateFactory();
        var app = await CreateAppAsync(factory, deployedOn: NewHost);

        // Simulate "already absent": the teardown stage throws the absent error.
        rig.FailAt = "teardown";

        var result = await RemoveAsync(factory, app.Id);

        // Best-effort by contract: teardown trouble never blocks deletion.
        Assert.Equal(AppOperationStatus.Succeeded, result.Operation!.Status);
        Assert.Contains("proceeding with record deletion", result.Operation.Log);
        Assert.Null(await ReloadAppAsync(factory, app.Id));
    }

    [Fact]
    public async Task RemoveWithNoDeployHost_SkipsTeardownAndDeletes()
    {
        await using var factory = CreateFactory();
        var app = await CreateAppAsync(factory, deployedOn: null);

        var result = await RemoveAsync(factory, app.Id);

        Assert.Equal(AppOperationStatus.Succeeded, result.Operation!.Status);
        Assert.Empty(rig.Calls);
        Assert.Null(await ReloadAppAsync(factory, app.Id));
        var op = Assert.Single(await OpsAsync(factory, app.Id));
        Assert.Contains("No deploy host recorded", op.Log);
    }

    [Fact]
    public async Task RemoveUnknownApp_IsRefused()
    {
        await using var factory = CreateFactory();

        var result = await RemoveAsync(factory, 999);

        Assert.False(result.Started);
        Assert.Empty(rig.Calls);
    }

    // ---- UI surface ----------------------------------------------------------

    [Fact]
    public async Task AppDetailPage_RendersLifecycleActions()
    {
        await using var factory = CreateFactory();
        var app = await CreateAppAsync(factory);

        var html = await factory.CreateClient().GetStringAsync($"/apps/{app.Id}");

        // The Move form and the Remove confirmation surface exist server-side
        // too — the remove path requires an explicit confirmation step, and
        // its initial render is the prompt, not the deletion.
        Assert.Contains("Move", html);
        Assert.Contains("Remove", html);
        Assert.Contains(OldHost, html); // deployment target shown on the page
    }

    // ---- Startup sweep --------------------------------------------------------

    [Fact]
    public void InterruptedOperations_AreFailedByStartupSweep_TerminalRowsUntouched()
    {
        using (var db = CreateSweepContext())
        {
            db.Database.Migrate();
            db.AppOperations.AddRange(
                new AppOperation { TrackedAppId = 1, Kind = AppOperationKind.Move, Status = AppOperationStatus.Queued, Log = "q" },
                new AppOperation { TrackedAppId = 1, Kind = AppOperationKind.Move, Status = AppOperationStatus.Deploying, Log = "deploying" },
                new AppOperation { TrackedAppId = 1, Kind = AppOperationKind.Remove, Status = AppOperationStatus.TearingDown, Log = "tearing down" },
                new AppOperation { TrackedAppId = 1, Kind = AppOperationKind.Move, Status = AppOperationStatus.Succeeded, Log = "done" },
                new AppOperation { TrackedAppId = 1, Kind = AppOperationKind.Move, Status = AppOperationStatus.Failed, Log = "already failed" });
            db.SaveChanges();
        }

        using (var db = CreateSweepContext())
        {
            Assert.Equal(3, AppLifecycleService.MarkInterruptedOperationsFailed(db));
        }

        using (var db = CreateSweepContext())
        {
            // 3 swept rows + the pre-existing failed row = 4 terminal failures.
            Assert.Equal(4, db.AppOperations.Count(o => o.Status == AppOperationStatus.Failed));
            Assert.Equal(1, db.AppOperations.Count(o => o.Status == AppOperationStatus.Succeeded));
            Assert.Contains(db.AppOperations, o => o.Log == "done");
            Assert.Contains(db.AppOperations, o => o.Log == "already failed");
        }
    }

    private HomelabDbContext CreateSweepContext()
    {
        var options = new DbContextOptionsBuilder<HomelabDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        return new HomelabDbContext(options);
    }
}
