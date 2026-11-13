using System.Net;
using HomelabInventory.Web.Models;
using HomelabInventory.Web.Services;
using HomelabInventory.Web.Services.Update;
using Microsoft.Extensions.DependencyInjection;

namespace HomelabInventory.Tests;

/// <summary>
/// Integration tests for the "Run update" pipeline: the real app hosted via
/// WebApplicationFactory against a temp SQLite file, with the three pipeline
/// interfaces (IGitSource, IImageBuilder, IDeployer) replaced by fakes —
/// no network, no docker, no SSH (acceptance criterion 6).
/// </summary>
public sealed class UpdateRunTests : IDisposable
{
    private readonly string dbPath =
        Path.Combine(Path.GetTempPath(), $"homelab-update-tests-{Guid.NewGuid():N}.db");

    private readonly FakePipelineRig rig = new();

    private UpdatePipelineFactory CreateFactory() => new(dbPath, rig);

    public void Dispose()
    {
        foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(dbPath) + "*"))
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    private async Task<TrackedApp> CreateUpdatableAppAsync(
        UpdatePipelineFactory factory,
        string name = "Grafana",
        Action<TrackedApp>? mutate = null)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TrackedAppService>();

        var app = new TrackedApp
        {
            Name = name,
            HostnameOrIp = "192.168.1.50:3000",
            RepoUrl = "https://github.com/samJwitt/grafana-docker.git",
            Branch = "main",
            ContainerImage = "grafana",
            DeployedOn = "192.168.1.50",
            Port = 3000,
        };
        mutate?.Invoke(app);

        var result = await service.CreateAsync(app);
        Assert.True(result.Success, string.Join("; ", result.Errors));
        return result.App!;
    }

    private UpdatePipelineService Pipeline(UpdatePipelineFactory factory) =>
        factory.Services.GetRequiredService<UpdatePipelineService>();

    // ---- happy path --------------------------------------------------------

    [Fact]
    public async Task HappyPath_DrivesRunThroughEveryStageAndSucceeds()
    {
        using var factory = CreateFactory();
        var app = await CreateUpdatableAppAsync(factory);

        var result = await Pipeline(factory).RunAsync(app.Id);

        Assert.True(result.Started);
        var run = result.Run!;
        Assert.Equal(UpdateRunStatus.Succeeded, run.Status);
        Assert.NotNull(run.CompletedAtUtc);
        Assert.Equal($"grafana:{rig.Sha}", run.ImageTag);

        // All three interfaces exercised, in pipeline order.
        Assert.Equal(["fetch", "build", "transfer", "restart"], rig.Calls);

        // The deployer received the VM host, image tag and container identity.
        var deployer = rig.Deployer;
        Assert.Equal(("192.168.1.50", "grafana", rig.Sha), deployer.Transferred);
        Assert.Equal("192.168.1.50", deployer.Restarted!.Value.Host);
        Assert.Equal($"grafana:{rig.Sha}", deployer.Restarted.Value.ImageTag);

        // The log captured each stage.
        Assert.Contains("fake fetch", run.Log, StringComparison.Ordinal);
        Assert.Contains("fake build", run.Log, StringComparison.Ordinal);
        Assert.Contains("fake transfer", run.Log, StringComparison.Ordinal);
        Assert.Contains("fake restart", run.Log, StringComparison.Ordinal);

        // Persisted: the run is readable back from a fresh scope, newest-first.
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TrackedAppService>();
        var history = await service.ListRunsAsync(app.Id);
        var stored = Assert.Single(history);
        Assert.Equal(run.Id, stored.Id);
        Assert.Equal(UpdateRunStatus.Succeeded, stored.Status);
    }

    // ---- per-stage failure -------------------------------------------------

    [Theory]
    [InlineData("fetch")]
    [InlineData("build")]
    [InlineData("transfer")]
    [InlineData("restart")]
    public async Task StageFailure_MarksRunFailedWithStageOutputAndStopsPipeline(string failAt)
    {
        using var factory = CreateFactory();
        var app = await CreateUpdatableAppAsync(factory);
        rig.FailAt = failAt;

        // The failing stage ran (and recorded its failure); later stages did not.
        var expectedCalls = new List<string> { "fetch", "build", "transfer", "restart" };
        expectedCalls.RemoveRange(expectedCalls.IndexOf(failAt) + 1,
            expectedCalls.Count - expectedCalls.IndexOf(failAt) - 1);

        var result = await Pipeline(factory).RunAsync(app.Id);

        Assert.True(result.Started); // the run record exists; it just failed
        var run = result.Run!;
        Assert.Equal(UpdateRunStatus.Failed, run.Status);
        Assert.NotNull(run.CompletedAtUtc);

        // The pipeline stopped at the failing stage — no later stages ran.
        Assert.Equal(expectedCalls, rig.Calls);

        // The failing stage's command output made it into the run log.
        Assert.Contains($"fake-{failAt}-output", run.Log, StringComparison.Ordinal);
        Assert.Contains("FAILED", run.Log, StringComparison.Ordinal);

        // The tracked app row itself is untouched by a failed update.
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TrackedAppService>();
        Assert.NotNull(await service.GetAsync(app.Id));
    }

    // ---- missing metadata: refuse before Building --------------------------

    [Theory]
    [InlineData("repo")]
    [InlineData("image")]
    [InlineData("deployedOn")]
    public async Task MissingMetadata_RefusesWithoutCreatingRunOrTouchingStages(string missing)
    {
        using var factory = CreateFactory();
        var app = await CreateUpdatableAppAsync(factory, mutate: a =>
        {
            switch (missing)
            {
                case "repo": a.RepoUrl = "  "; break;
                case "image": a.ContainerImage = null; break;
                case "deployedOn": a.DeployedOn = null; break;
            }
        });

        var result = await Pipeline(factory).RunAsync(app.Id);

        Assert.False(result.Started);
        Assert.Contains(result.Errors, e => e.Contains("cannot run an update"));

        // No run row was created and no pipeline stage executed.
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TrackedAppService>();
        Assert.Empty(await service.ListRunsAsync(app.Id));
        Assert.Empty(rig.Calls);
    }

    // ---- history + UI --------------------------------------------------------

    [Fact]
    public async Task MultipleRuns_AreListedNewestFirst_AndDetailPageRendersUpdatePanel()
    {
        using var factory = CreateFactory();
        var app = await CreateUpdatableAppAsync(factory);

        var first = await Pipeline(factory).RunAsync(app.Id);
        var second = await Pipeline(factory).RunAsync(app.Id);
        Assert.True(first.Started && second.Started);

        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TrackedAppService>();
        var history = await service.ListRunsAsync(app.Id);
        Assert.Equal(2, history.Count);
        Assert.True(history[0].StartedAtUtc >= history[1].StartedAtUtc);
        Assert.Equal(second.Run!.Id, history[0].Id);

        // The detail page prerenders the interactive update panel shell with
        // both runs already in history (the panel ran its own query server-side).
        var client = factory.CreateClient();
        var html = await client.GetStringAsync($"/apps/{app.Id}");
        Assert.Contains("Run update", html, StringComparison.Ordinal);
        Assert.Contains("Succeeded", html, StringComparison.Ordinal);
        Assert.DoesNotContain("No updates yet", html, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync($"/apps/{app.Id}")).StatusCode);
    }

    // ---- migrations replace EnsureCreated ------------------------------------

    [Fact]
    public async Task Startup_AppliesMigrations_SchemaIncludesUpdateRuns()
    {
        using var factory = CreateFactory();
        _ = factory.Services; // force host startup (applies migrations)
        Assert.True(File.Exists(dbPath));

        await using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                         $"Data Source={dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='UpdateRuns'";
            var count = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            Assert.Equal(1, count);

            // The migration history table proves Migrate(), not EnsureCreated().
            cmd.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory'";
            count = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            Assert.Equal(1, count);
        }
    }
}
