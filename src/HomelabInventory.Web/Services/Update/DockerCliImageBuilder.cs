namespace HomelabInventory.Web.Services.Update;

/// <summary>
/// Real <see cref="IImageBuilder"/> driving the <c>docker</c> CLI (the chosen
/// container runtime — see README; swap this class to move to podman).
/// </summary>
public sealed class DockerCliImageBuilder : IImageBuilder
{
    public Task BuildAsync(
        string image,
        string tag,
        string contextDir,
        Action<string> log,
        CancellationToken ct = default) =>
        ProcessRunner.RunAsync(
            "build", "docker",
            ["build", "-t", $"{image}:{tag}", contextDir], log, ct);
}
