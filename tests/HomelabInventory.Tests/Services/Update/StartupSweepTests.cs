using HomelabInventory.Web.Data;
using HomelabInventory.Web.Models;
using HomelabInventory.Web.Services.Update;
using Microsoft.EntityFrameworkCore;

namespace HomelabInventory.Tests.Services.Update;

/// <summary>
/// The startup sweep: rows left in a non-terminal status by a killed process
/// must come back as Failed('interrupted'), while terminal rows are untouched.
/// Uses throwaway SQLite files (same persistence as the app, still hermetic).
/// </summary>
public class StartupSweepTests : IDisposable
{
    private readonly string dbPath =
        Path.Combine(Path.GetTempPath(), $"homelab-sweep-{Guid.NewGuid():N}.db");

    private HomelabDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<HomelabDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        return new HomelabDbContext(options);
    }

    private int SeedRuns()
    {
        using var db = CreateContext();
        // Migrate (not EnsureCreated) so the seeded file matches what a real
        // run leaves behind — booting the app over it must be a no-op.
        db.Database.Migrate();
        var app = new TrackedApp
        {
            Name = "Swept",
            NormalizedName = "swept",
            HostnameOrIp = "127.0.0.1",
        };
        db.TrackedApps.Add(app);
        db.SaveChanges();

        db.UpdateRuns.AddRange(
            new UpdateRun { TrackedAppId = app.Id, Status = UpdateRunStatus.Queued, Log = "q" },
            new UpdateRun { TrackedAppId = app.Id, Status = UpdateRunStatus.Building, Log = "building step" },
            new UpdateRun { TrackedAppId = app.Id, Status = UpdateRunStatus.Deploying, Log = "d" },
            new UpdateRun { TrackedAppId = app.Id, Status = UpdateRunStatus.Succeeded, Log = "done" },
            new UpdateRun { TrackedAppId = app.Id, Status = UpdateRunStatus.Failed, Log = "already failed" });
        return db.SaveChanges();
    }

    [Fact]
    public void Sweep_FailsNonTerminalRuns_LeavesTerminalRowsAlone()
    {
        SeedRuns();

        using (var db = CreateContext())
        {
            Assert.Equal(3, UpdatePipelineService.MarkInterruptedRunsFailed(db));
        }

        using (var db = CreateContext())
        {
            var runs = db.UpdateRuns.AsNoTracking()
                .OrderBy(r => r.Id)
                .Select(r => new { r.Status, r.Log, r.CompletedAtUtc })
                .ToList();

            Assert.All(runs.Take(3), r =>
            {
                Assert.Equal(UpdateRunStatus.Failed, r.Status);
                Assert.Contains("Interrupted", r.Log);
                Assert.NotNull(r.CompletedAtUtc);
            });
            Assert.Contains("building step", runs[1].Log); // prior output preserved

            Assert.Equal(UpdateRunStatus.Succeeded, runs[3].Status);
            Assert.Equal("done", runs[3].Log);
            Assert.Equal(UpdateRunStatus.Failed, runs[4].Status);
            Assert.Equal("already failed", runs[4].Log);
        }
    }

    [Fact]
    public void Sweep_IsIdempotent_AndNoOpsOnCleanDatabase()
    {
        SeedRuns();
        using var db = CreateContext();
        UpdatePipelineService.MarkInterruptedRunsFailed(db);

        // Second pass sweeps nothing: everything is terminal now.
        using var db2 = CreateContext();
        Assert.Equal(0, UpdatePipelineService.MarkInterruptedRunsFailed(db2));
    }

    [Fact]
    public async Task Boot_AutomaticallySweepsRunsLeftBehindByPreviousProcess()
    {
        SeedRuns();

        // Booting the real app runs the startup guard, which includes the sweep.
        using var factory = new HomelabWebAppFactory(dbPath);
        using var client = factory.CreateClient();
        var page = await client.GetStringAsync("/");
        Assert.NotEmpty(page);

        using var db = CreateContext();
        var nonTerminal = await db.UpdateRuns.AsNoTracking()
            .CountAsync(r => r.Status != UpdateRunStatus.Succeeded
                             && r.Status != UpdateRunStatus.Failed);
        Assert.Equal(0, nonTerminal);
    }

    public void Dispose()
    {
        if (File.Exists(dbPath))
        {
            File.Delete(dbPath);
        }
    }
}
