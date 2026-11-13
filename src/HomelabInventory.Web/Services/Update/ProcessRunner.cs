using System.Diagnostics;

namespace HomelabInventory.Web.Services.Update;

/// <summary>
/// Runs external CLI commands capturing combined output into the run log.
/// Lives here so the three real stage implementations share one failure path.
/// </summary>
internal static class ProcessRunner
{
    public static async Task<string> RunAsync(
        string step,
        string fileName,
        IReadOnlyList<string> arguments,
        Action<string> log,
        CancellationToken ct,
        bool ignoreExitCode = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        log($"> {fileName} {string.Join(' ', arguments)}");

        using var process = Process.Start(startInfo)
            ?? throw new UpdateStepException(step, $"Could not start '{fileName}'.");

        // Read both pipes concurrently: reading them sequentially deadlocks
        // when one fills while the other blocks the process.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            log(stdout.TrimEnd());
        }
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            log(stderr.TrimEnd());
        }

        if (process.ExitCode != 0 && !ignoreExitCode)
        {
            throw new UpdateStepException(
                step,
                $"'{fileName}' exited with code {process.ExitCode}",
                $"{stdout}{stderr}");
        }

        return stdout.Trim();
    }
}
