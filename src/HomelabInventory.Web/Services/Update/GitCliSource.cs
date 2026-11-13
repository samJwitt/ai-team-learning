namespace HomelabInventory.Web.Services.Update;

/// <summary>
/// Real <see cref="IGitSource"/> driving the system <c>git</c> CLI: shallow
/// clone on first run, fetch + hard reset afterwards, so the working dir always
/// ends exactly at the branch tip.
/// </summary>
public sealed class GitCliSource : IGitSource
{
    public async Task<string> FetchLatestAsync(
        string repoUrl,
        string branch,
        string workingDir,
        Action<string> log,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(Path.Combine(workingDir, ".git")))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(workingDir)!);
            await ProcessRunner.RunAsync(
                "fetch", "git",
                ["clone", "--branch", branch, repoUrl, workingDir], log, ct);
        }
        else
        {
            await ProcessRunner.RunAsync(
                "fetch", "git", ["-C", workingDir, "fetch", "origin", branch], log, ct);
            await ProcessRunner.RunAsync(
                "fetch", "git", ["-C", workingDir, "checkout", branch], log, ct);
            await ProcessRunner.RunAsync(
                "fetch", "git", ["-C", workingDir, "reset", "--hard", $"origin/{branch}"], log, ct);
        }

        var sha = await ProcessRunner.RunAsync(
            "fetch", "git", ["-C", workingDir, "rev-parse", "--short", "HEAD"], log, ct);
        log($"Checked out commit {sha} in {workingDir}");
        return sha;
    }
}
