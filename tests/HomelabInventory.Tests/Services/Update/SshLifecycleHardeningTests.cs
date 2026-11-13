using HomelabInventory.Web.Services.Update;

namespace HomelabInventory.Tests.Services.Update;

/// <summary>
/// Command-construction hardening for the lifecycle additions to
/// <see cref="SshDockerDeployer"/> (IsContainerRunningAsync / TeardownAsync)
/// and the shared <see cref="DeployToken"/> allowlist. Validation runs BEFORE
/// any process spawn, so these tests are hermetic: a hostile token must be
/// refused with nothing executed (proven by the canary file never appearing).
/// </summary>
public class SshLifecycleHardeningTests
{
    // ---- DeployToken allowlist ---------------------------------------------

    [Theory]
    [InlineData("192.0.2.10")]
    [InlineData("nas.local")]
    [InlineData("vm-1.internal")]
    [InlineData("hl-grafana-3")]
    [InlineData("grafana")]
    [InlineData("registry.local:5000/team/app")]
    [InlineData("tag_1.2:latest")]
    public void SafeTokens_AreAccepted(string token)
    {
        Assert.True(DeployToken.IsValid(token));
    }

    [Theory]
    // Shell metacharacters and command substitution.
    [InlineData("host; rm -rf /")]
    [InlineData("host && touch /tmp/x")]
    [InlineData("host `id`")]
    [InlineData("host $(id)")]
    [InlineData("host|cat")]
    [InlineData("host\nrm")]
    [InlineData("host'")]
    [InlineData("host\"")]
    // Leading dash: would land in argv as an option.
    [InlineData("-l")]
    [InlineData("--help")]
    // Empty / whitespace / globbing.
    [InlineData("")]
    [InlineData("host name")]
    [InlineData("host*")]
    [InlineData(null)]
    public void HostileTokens_AreRejected(string? token)
    {
        Assert.False(DeployToken.IsValid(token));
    }

    // ---- Teardown / status-check hardening (spawn-free) ----------------------

    [Theory]
    [InlineData("192.0.2.10; rm -rf /tmp/hermes-teardown-pwned", "hl-app-1")]
    [InlineData("192.0.2.10", "hl-app-1 && touch /tmp/hermes-teardown-pwned")]
    [InlineData("192.0.2.10", "--volumes")]
    [InlineData("-oProxyCommand=evil", "hl-app-1")]
    public async Task Teardown_HostileTokens_ThrowBeforeSpawning(string host, string container)
    {
        var lines = new List<string>();

        var ex = await Assert.ThrowsAsync<UpdateStepException>(() =>
            new SshDockerDeployer().TeardownAsync(host, container, lines.Add));

        Assert.Equal("deploy", ex.Step);
        Assert.False(File.Exists("/tmp/hermes-teardown-pwned"));
    }

    [Theory]
    [InlineData("192.0.2.10; curl evil", "hl-app-1")]
    [InlineData("192.0.2.10", "$(reboot)")]
    public async Task StatusCheck_HostileTokens_ThrowBeforeSpawning(string host, string container)
    {
        var lines = new List<string>();

        var ex = await Assert.ThrowsAsync<UpdateStepException>(() =>
            new SshDockerDeployer().IsContainerRunningAsync(host, container, lines.Add));

        Assert.Equal("deploy", ex.Step);
    }

    [Fact]
    public async Task Teardown_CanaryCommand_NeverBuiltIntoShellString()
    {
        // Defense in depth: even a token that merely looks option-ish must be
        // refused rather than quoted-and-passed; and the deployer must never
        // emit a log line echoing the hostile fragment as an executed command.
        const string hostile = "x; touch /tmp/hermes-shell-pwned";
        var lines = new List<string>();

        await Assert.ThrowsAsync<UpdateStepException>(() =>
            new SshDockerDeployer().TeardownAsync(hostile, "hl-app-1", lines.Add));

        Assert.DoesNotContain(lines, l => l.StartsWith("> ", StringComparison.Ordinal));
        Assert.False(File.Exists("/tmp/hermes-shell-pwned"));
    }
}
