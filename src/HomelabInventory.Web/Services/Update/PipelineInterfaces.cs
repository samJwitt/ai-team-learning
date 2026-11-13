namespace HomelabInventory.Web.Services.Update;

/// <summary>
/// Raised by a pipeline step when the underlying command fails. The failing
/// step's captured output rides along so the pipeline can persist it into the
/// run log (acceptance criterion 4).
/// </summary>
public sealed class UpdateStepException(string step, string message, string output = "")
    : Exception($"{step}: {message}")
{
    /// <summary>The pipeline stage that failed (fetch, build, push, deploy).</summary>
    public string Step { get; } = step;

    /// <summary>Stdout+stderr of the failing command, as captured.</summary>
    public string Output { get; } = output;
}

/// <summary>
/// Stage 1 of the update pipeline: bring the app's branch up to date locally.
/// </summary>
public interface IGitSource
{
    /// <summary>
    /// Fetch the latest <paramref name="branch"/> of <paramref name="repoUrl"/>
    /// into <paramref name="workingDir"/> (cloning on first run) and report the
    /// checked-out short commit SHA. Throws <see cref="UpdateStepException"/> on failure.
    /// </summary>
    Task<string> FetchLatestAsync(
        string repoUrl,
        string branch,
        string workingDir,
        Action<string> log,
        CancellationToken ct = default);
}

/// <summary>
/// Stage 2 of the update pipeline: produce <c>{image}:{tag}</c> locally.
/// </summary>
public interface IImageBuilder
{
    /// <summary>
    /// Build the container image <c>{image}:{tag}</c> from the Dockerfile in
    /// <paramref name="contextDir"/>. Throws <see cref="UpdateStepException"/> on failure.
    /// </summary>
    Task BuildAsync(
        string image,
        string tag,
        string contextDir,
        Action<string> log,
        CancellationToken ct = default);
}

/// <summary>
/// Stage 3 of the update pipeline: get the built image onto the hosting VM and
/// restart the app's container there.
/// </summary>
public interface IDeployer
{
    /// <summary>
    /// Transfer the locally built <c>{image}:{tag}</c> to the container host
    /// <paramref name="targetHost"/>. Throws <see cref="UpdateStepException"/> on failure.
    /// </summary>
    Task TransferImageAsync(
        string targetHost,
        string image,
        string tag,
        Action<string> log,
        CancellationToken ct = default);

    /// <summary>
    /// Recreate the container <paramref name="containerName"/> on
    /// <paramref name="targetHost"/> from <paramref name="imageTag"/>,
    /// publishing <paramref name="hostPort"/> when given. Throws
    /// <see cref="UpdateStepException"/> on failure.
    /// </summary>
    Task RestartContainerAsync(
        string targetHost,
        string containerName,
        string imageTag,
        int? hostPort,
        Action<string> log,
        CancellationToken ct = default);

    /// <summary>
    /// True when containerName exists on targetHost and its docker state is
    /// running. Any inspect failure (host unreachable, container missing)
    /// reports false rather than throwing — callers treat "not verified up"
    /// as a failed deployment.
    /// </summary>
    Task<bool> IsContainerRunningAsync(
        string targetHost,
        string containerName,
        Action<string> log,
        CancellationToken ct = default);

    /// <summary>
    /// Stop and remove containerName on targetHost. An already-absent
    /// container is treated as success (teardown is idempotent); other
    /// failures throw <see cref="UpdateStepException"/> with step "deploy"
    /// (same step namespace as the rest of <see cref="IDeployer"/>).
    /// </summary>
    Task TeardownAsync(
        string targetHost,
        string containerName,
        Action<string> log,
        CancellationToken ct = default);
}
