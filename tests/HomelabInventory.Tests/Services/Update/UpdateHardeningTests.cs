using HomelabInventory.Web.Models;
using HomelabInventory.Web.Services.Update;

namespace HomelabInventory.Tests.Services.Update;

/// <summary>
/// Unit tests for the alignment-hardening helpers that guard the update
/// pipeline: RepoUrl scheme allowlisting, run-log capping, and collision-free
/// container naming. All pure logic — no processes, network, or docker.
/// </summary>
public class UpdateHardeningTests
{
    // ---- RepoUrl scheme allowlist ----------------------------------------

    [Theory]
    [InlineData("https://github.com/me/app.git")]
    [InlineData("HTTP://host/repo")]
    [InlineData("git://git.kernel.org/pub/scm/x/y.git")]
    [InlineData("ssh://git@host:2222/team/repo.git")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AllowedRepoUrls_AreAccepted(string? url)
    {
        Assert.True(GitRepoUrl.IsAllowed(url));
    }

    [Theory]
    // The git-argv injection class: option-like values in URL position.
    [InlineData("--upload-pack=touch /tmp/pwned")]
    [InlineData(" -C /some/dir")]
    // The ext:: transport is arbitrary code execution.
    [InlineData("ext::sh -c 'touch /tmp/pwned'")]
    // Local filesystem and other transports are out of scope for this app.
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://host/repo")]
    // scp-style syntax has no scheme; users must spell ssh:// explicitly.
    [InlineData("git@github.com:me/app.git")]
    [InlineData("/local/path/to/repo")]
    public void HostileOrUnsupportedRepoUrls_AreRejected(string url)
    {
        Assert.False(GitRepoUrl.IsAllowed(url));
    }

    [Fact]
    public async Task GitCliSource_RejectsDisallowedUrl_SpawningNothing()
    {
        var workingDir = Path.Combine(Path.GetTempPath(), $"hl-gitguard-{Guid.NewGuid():N}", "repo");
        var lines = new List<string>();

        var ex = await Assert.ThrowsAsync<UpdateStepException>(() =>
            new GitCliSource().FetchLatestAsync(
                "ext::sh -c 'touch /tmp/hermes-git-pwned'", "main", workingDir, lines.Add));

        Assert.Equal("fetch", ex.Step);
        Assert.DoesNotContain("/tmp/hermes-git-pwned", string.Join("\n", lines));
        Assert.False(File.Exists("/tmp/hermes-git-pwned"));
        Assert.False(Directory.Exists(workingDir));
    }

    // ---- UpdateRunLog capping --------------------------------------------

    [Fact]
    public void Cap_LeavesShortLogsUntouched()
    {
        const string log = "build ok\ndeploy ok";
        Assert.Equal(log, UpdateRunLog.Cap(log));
    }

    [Fact]
    public void Cap_LogOfExactlyMaxChars_IsUntouched()
    {
        var log = new string('a', UpdateRunLog.MaxChars);
        Assert.Equal(log, UpdateRunLog.Cap(log));
    }

    [Fact]
    public void Cap_LongLog_KeepsHeadAndTailWithElisionMarker()
    {
        // Head and tail are longer than the keep-slices, so everything
        // between the first and last halves must vanish.
        var head = "HEAD-MARKER " + new string('h', 40000);
        var middle = new string('m', 80000);
        var tail = new string('t', 40000) + " TAIL-MARKER";

        var capped = UpdateRunLog.Cap(head + middle + tail);

        Assert.True(capped.Length <= UpdateRunLog.MaxChars);
        Assert.StartsWith("HEAD-MARKER", capped);
        Assert.EndsWith("TAIL-MARKER", capped);
        Assert.Contains("characters elided", capped);
        Assert.DoesNotContain('m', capped);
    }

    // ---- Container naming -------------------------------------------------

    [Fact]
    public void ContainerName_DistinguishesAppsWhoseNamesSlugIdentically()
    {
        var a = UpdatePipelineService.ContainerName(
            new TrackedApp { Id = 1, Name = "My App" });
        var b = UpdatePipelineService.ContainerName(
            new TrackedApp { Id = 2, Name = "my-app" });

        Assert.Equal("hl-my-app-1", a);
        Assert.Equal("hl-my-app-2", b);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ContainerName_IsDeterministicPerApp()
    {
        var app = new TrackedApp { Id = 7, Name = "Home Assistant" };
        Assert.Equal(
            UpdatePipelineService.ContainerName(app),
            UpdatePipelineService.ContainerName(app));
    }

    [Fact]
    public void ContainerName_IsValidDockerNameCharacters()
    {
        var name = UpdatePipelineService.ContainerName(
            new TrackedApp { Id = 3, Name = "Grafana!! v2 (prod)" });

        Assert.Matches("^[a-z0-9][a-z0-9-]*$", name);
        Assert.EndsWith("-3", name);
    }
}
