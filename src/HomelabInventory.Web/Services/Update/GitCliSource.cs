using HomelabInventory.Web.Models;

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
        // Defense in depth: the form validates RepoUrl, but the adapter is the
        // last point before git sees argv, so it re-checks the scheme here.
        // This rejects the ext::/--upload-pack= class outright.
        if (!GitRepoUrl.IsAllowed(repoUrl))
        {
            throw new UpdateStepException(
                "fetch", $"Repository URL '{repoUrl}' uses a disallowed scheme.");
        }

        if (!Directory.Exists(Path.Combine(workingDir, ".git")))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(workingDir)!);
            // "--" ends option parsing so repoUrl/branch can never be read
            // as git options regardless of what they start with.
            await ProcessRunner.RunAsync(
                "fetch", "git",
                ["clone", "--branch", branch, "--", repoUrl, workingDir], log, ct);
        }
        else
        {
            // --end-of-options (git >= 2.24): branch is user data sitting in
            // rev/refspec position; force it to be read as a revision only.
            await ProcessRunner.RunAsync(
                "fetch", "git",
                ["-C", workingDir, "fetch", "--end-of-options", "origin", branch], log, ct);
            await ProcessRunner.RunAsync(
                "fetch", "git",
                ["-C", workingDir, "checkout", "--end-of-options", branch], log, ct);
            // Safe without a separator: the rev always starts with "origin/",
            // so it can never be parsed as an option.
            await ProcessRunner.RunAsync(
                "fetch", "git", ["-C", workingDir, "reset", "--hard", $"origin/{branch}"], log, ct);
        }

        var sha = await ProcessRunner.RunAsync(
            "fetch", "git", ["-C", workingDir, "rev-parse", "--short", "HEAD"], log, ct);
        log($"Checked out commit {sha} in {workingDir}");
        return sha;
    }
}
