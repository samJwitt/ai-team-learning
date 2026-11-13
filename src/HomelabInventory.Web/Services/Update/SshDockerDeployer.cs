namespace HomelabInventory.Web.Services.Update;

/// <summary>
/// Real <see cref="IDeployer"/> for docker hosts reachable over key-based SSH
/// (no credentials stored in the app). The image moves through a pipe —
/// <c>docker save</c> locally into <c>ssh &lt;vm&gt; docker load</c> — so no
/// registry is required on the LAN.
/// </summary>
public sealed class SshDockerDeployer : IDeployer
{
    private static readonly string[] SshOptions = ["-o", "BatchMode=yes", "-o", "ConnectTimeout=15"];

    public async Task TransferImageAsync(
        string targetHost,
        string image,
        string tag,
        Action<string> log,
        CancellationToken ct = default)
    {
        EnsureSafe(targetHost);
        EnsureSafe(image);
        EnsureSafe(tag);

        log($"> docker save {image}:{tag} | ssh {targetHost} docker load");

        var sshStart = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ssh",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in SshOptions.Append(targetHost).Append("docker load"))
        {
            sshStart.ArgumentList.Add(arg);
        }

        var saveStart = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "save", $"{image}:{tag}" })
        {
            saveStart.ArgumentList.Add(arg);
        }

        using var ssh = System.Diagnostics.Process.Start(sshStart)
            ?? throw new UpdateStepException("push", "Could not start 'ssh'.");
        using var save = System.Diagnostics.Process.Start(saveStart)
            ?? throw new UpdateStepException("push", "Could not start 'docker save'.");

        var sshOut = ssh.StandardOutput.ReadToEndAsync(ct);
        var sshErr = ssh.StandardError.ReadToEndAsync(ct);
        var saveErr = save.StandardError.ReadToEndAsync(ct);

        await save.StandardOutput.BaseStream.CopyToAsync(ssh.StandardInput.BaseStream, ct);
        ssh.StandardInput.Dispose();

        await save.WaitForExitAsync(ct);
        await ssh.WaitForExitAsync(ct);

        var saveErrText = await saveErr;
        var sshOutText = await sshOut;
        var sshErrText = await sshErr;

        if (!string.IsNullOrWhiteSpace(sshOutText))
        {
            log(sshOutText.TrimEnd());
        }
        if (!string.IsNullOrWhiteSpace(saveErrText))
        {
            log(saveErrText.TrimEnd());
        }
        if (!string.IsNullOrWhiteSpace(sshErrText))
        {
            log(sshErrText.TrimEnd());
        }

        if (save.ExitCode != 0)
        {
            throw new UpdateStepException(
                "push", $"'docker save' exited with code {save.ExitCode}", saveErrText);
        }
        if (ssh.ExitCode != 0)
        {
            throw new UpdateStepException(
                "push", $"'ssh {targetHost} docker load' exited with code {ssh.ExitCode}",
                $"{sshOutText}{sshErrText}");
        }
    }

    public async Task RestartContainerAsync(
        string targetHost,
        string containerName,
        string imageTag,
        int? hostPort,
        Action<string> log,
        CancellationToken ct = default)
    {
        EnsureSafe(targetHost);
        EnsureSafe(containerName);
        EnsureSafe(imageTag);

        // Remove the old container first; failing here only means it never
        // existed, which is not an error for a first deploy.
        await ProcessRunner.RunAsync(
            "deploy", "ssh",
            SshOptions.Append(targetHost).Append($"docker rm -f {containerName}").ToArray(),
            log, ct, ignoreExitCode: true);

        var portArgs = hostPort is > 0 ? $"-p {hostPort}:{hostPort} " : "";
        await ProcessRunner.RunAsync(
            "deploy", "ssh",
            SshOptions.Append(targetHost).Append(
                $"docker run -d --name {containerName} --restart unless-stopped {portArgs}{imageTag}")
                .ToArray(),
            log, ct);
    }

    public async Task<bool> IsContainerRunningAsync(
        string targetHost,
        string containerName,
        Action<string> log,
        CancellationToken ct = default)
    {
        EnsureSafe(targetHost);
        EnsureSafe(containerName);

        // Single argv to ssh; the remote command string contains only the
        // allowlisted container name plus a static quoted format. '--' ends
        // options so even allowlisted-looking values can't become flags.
        // ignoreExitCode: an unreachable host or missing container is
        // "not up", not a step failure — the caller decides what up/false means.
        var output = await ProcessRunner.RunAsync(
            "deploy", "ssh",
            SshOptions.Append(targetHost).Append(
                $"docker inspect -f '{{{{.State.Running}}}}' -- {containerName}").ToArray(),
            log, ct, ignoreExitCode: true);

        return output == "true";
    }

    public async Task TeardownAsync(
        string targetHost,
        string containerName,
        Action<string> log,
        CancellationToken ct = default)
    {
        EnsureSafe(targetHost);
        EnsureSafe(containerName);

        try
        {
            await ProcessRunner.RunAsync(
                "deploy", "ssh",
                SshOptions.Append(targetHost).Append(
                    $"docker rm -f -- {containerName}").ToArray(),
                log, ct);
        }
        catch (UpdateStepException ex)
        {
            // Removal semantics are "gone afterwards": docker exits non-zero
            // with "No such container" when it is already absent, which is
            // success for a teardown. Anything else (unreachable host,
            // permission error) stays a failure for the caller to grade.
            if (ex.Output.Contains("No such container", StringComparison.OrdinalIgnoreCase))
            {
                log($"Container '{containerName}' already gone on {targetHost}.");
                return;
            }

            throw;
        }
    }

    private static void EnsureSafe(string token)
    {
        if (!DeployToken.IsValid(token))
        {
            throw new UpdateStepException(
                "deploy", $"'{token}' is not a valid host/image/container token.");
        }
    }
}
