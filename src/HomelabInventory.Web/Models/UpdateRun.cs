namespace HomelabInventory.Web.Models;

/// <summary>
/// Lifecycle of a single "Run update" pipeline execution. The pipeline walks
/// <see cref="Building"/> → <see cref="Pushing"/> → <see cref="Deploying"/> →
/// <see cref="Succeeded"/>; any failing step ends the run in
/// <see cref="Failed"/> with the failing step's output in
/// <see cref="UpdateRun.Log"/>.
/// </summary>
public enum UpdateRunStatus
{
    Queued,
    Building,
    Pushing,
    Deploying,
    Succeeded,
    Failed,
}

/// <summary>
/// One recorded attempt to rebuild and redeploy a tracked app from its source
/// repository. Runs are append-only history: deleting a tracked app removes
/// its run history with it (cascade).
/// </summary>
public class UpdateRun
{
    public int Id { get; set; }

    public int TrackedAppId { get; set; }
    public TrackedApp? TrackedApp { get; set; }

    public UpdateRunStatus Status { get; set; } = UpdateRunStatus.Queued;

    /// <summary>When the run was accepted by the pipeline (UTC).</summary>
    public DateTime StartedAtUtc { get; set; }

    /// <summary>When the run reached a terminal status (UTC); null while running.</summary>
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>
    /// Full image reference <c>{ContainerImage}:{short-sha}</c>, set once the
    /// commit SHA is known; null for a run that failed before the fetch step.
    /// </summary>
    public string? ImageTag { get; set; }

    /// <summary>Human-readable log of every step, including the failing output.</summary>
    public string Log { get; set; } = "";
}
