using HomelabInventory.Web.Services.Update;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HomelabInventory.Tests.Services.Update;

/// <summary>
/// Shared recorder + failure switch for the pipeline fakes. Set
/// <see cref="FailAt"/> to one of fetch/build/transfer/restart/teardown to
/// make that stage throw with canned command output.
/// </summary>
public sealed class FakePipelineRig
{
    /// <summary>Stage names in call order, e.g. ["fetch", "build", ...].</summary>
    public List<string> Calls { get; } = [];

    public string Sha { get; set; } = "abc1234";

    /// <summary>Stage that should fail, or null for a fully successful pipeline.</summary>
    public string? FailAt { get; set; }

    public bool ShouldFail(string stage) =>
        string.Equals(FailAt, stage, StringComparison.OrdinalIgnoreCase);

    public void Fail(string stage, string message, string output)
    {
        if (ShouldFail(stage))
        {
            throw new UpdateStepException(stage, message, output);
        }
    }

    public string Marker(string stage) => $"fake-{stage}-output";

    public FakeGitSource Git { get; }
    public FakeImageBuilder Builder { get; }
    public FakeDeployer Deployer { get; }

    public FakePipelineRig()
    {
        Git = new FakeGitSource(this);
        Builder = new FakeImageBuilder(this);
        Deployer = new FakeDeployer(this);
    }
}

public sealed class FakeGitSource(FakePipelineRig rig) : IGitSource
{
    public Task<string> FetchLatestAsync(
        string repoUrl, string branch, string workingDir,
        Action<string> log, CancellationToken ct = default)
    {
        rig.Calls.Add("fetch");
        rig.Fail("fetch", "could not fetch branch", rig.Marker("fetch"));
        log($"fake fetch {repoUrl}@{branch} -> {rig.Sha}");
        return Task.FromResult(rig.Sha);
    }
}

public sealed class FakeImageBuilder(FakePipelineRig rig) : IImageBuilder
{
    public Task BuildAsync(
        string image, string tag, string contextDir,
        Action<string> log, CancellationToken ct = default)
    {
        rig.Calls.Add("build");
        rig.Fail("build", "image build failed", rig.Marker("build"));
        log($"fake build {image}:{tag} from {contextDir}");
        return Task.CompletedTask;
    }
}

public sealed class FakeDeployer(FakePipelineRig rig) : IDeployer
{
    public (string Host, string Image, string Tag)? Transferred { get; private set; }
    public (string Host, string Container, string ImageTag, int? Port)? Restarted { get; private set; }

    /// <summary>What a subsequent container-status check reports.</summary>
    public bool ContainerRunning { get; set; } = true;

    /// <summary>Every teardown executed, in call order.</summary>
    public List<(string Host, string Container)> Teardowns { get; } = [];

    public Task TransferImageAsync(
        string targetHost, string image, string tag,
        Action<string> log, CancellationToken ct = default)
    {
        rig.Calls.Add("transfer");
        rig.Fail("transfer", "image transfer failed", rig.Marker("transfer"));
        Transferred = (targetHost, image, tag);
        log($"fake transfer {image}:{tag} -> {targetHost}");
        return Task.CompletedTask;
    }

    public Task RestartContainerAsync(
        string targetHost, string containerName, string imageTag, int? hostPort,
        Action<string> log, CancellationToken ct = default)
    {
        rig.Calls.Add("restart");
        rig.Fail("restart", "container restart failed", rig.Marker("restart"));
        Restarted = (targetHost, containerName, imageTag, hostPort);
        log($"fake restart {containerName} on {targetHost}");
        return Task.CompletedTask;
    }

    public Task<bool> IsContainerRunningAsync(
        string targetHost, string containerName,
        Action<string> log, CancellationToken ct = default)
    {
        rig.Calls.Add("verify");
        log($"fake verify {containerName} on {targetHost}: {ContainerRunning}");
        return Task.FromResult(ContainerRunning);
    }

    public Task TeardownAsync(
        string targetHost, string containerName,
        Action<string> log, CancellationToken ct = default)
    {
        rig.Calls.Add("teardown");
        rig.Fail("teardown", "container teardown failed", rig.Marker("teardown"));
        Teardowns.Add((targetHost, containerName));
        log($"fake teardown {containerName} on {targetHost}");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Factory that swaps all three real pipeline stages for the fakes above —
/// no network, no docker, no SSH in tests (acceptance criterion 6).
/// </summary>
public sealed class UpdatePipelineFactory(string databasePath, FakePipelineRig rig)
    : HomelabWebAppFactory(databasePath)
{
    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.RemoveAll<IGitSource>();
        services.RemoveAll<IImageBuilder>();
        services.RemoveAll<IDeployer>();
        services.AddSingleton<IGitSource>(rig.Git);
        services.AddSingleton<IImageBuilder>(rig.Builder);
        services.AddSingleton<IDeployer>(rig.Deployer);
    }
}
