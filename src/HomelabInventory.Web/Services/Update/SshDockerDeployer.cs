using System.Text.RegularExpressions;

namespace HomelabInventory.Web.Services.Update;

/// <summary>
/// Real <see cref="IDeployer"/> for docker hosts reachable over key-based SSH
/// (no credentials stored in the app). The image moves through a pipe —
/// <c>docker save</c> locally into <c>ssh &lt;vm&gt; docker load</c> — so no
/// registry is required on the LAN.
/// </summary>
public sealed partial class SshDockerDeployer : IDeployer
{
    private static readonly string[] SshOptions = ["-o", "BatchMode=yes", "-o", "ConnectTimeout=15"];

    // Everything interpolated into a remote command must be a plain
    // host/image/container token; refuse anything with shell metacharacters.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:@/-]*$")]
    private static partial Regex SafeToken();

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

    private static void EnsureSafe(string token)
    {
        if (!SafeToken().IsMatch(token))
        {
            throw new UpdateStepException(
                "deploy", $"'{token}' is not a valid host/image/container token.");
        }
    }
}
