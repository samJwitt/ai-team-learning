namespace HomelabInventory.Web.Models;

public enum AppOperationKind
{
    Move,
    Remove,
}

public enum AppOperationStatus
{
    Queued,
    Deploying,
    TearingDown,
    Succeeded,
    Failed,
}

/// <summary>
/// One audited lifecycle operation (Move or Remove) against an inventory app.
/// Follows the UpdateRun pattern: timestamped status transitions plus a capped
/// step log so every host/port change is explainable after the fact.
///
/// Deliberately has NO foreign key to <see cref="TrackedApp"/>: a Remove must
/// stay auditable after the app row itself is deleted, so TrackedAppId is kept
/// as a plain (non-enforced) reference on purpose. Do not "fix" this into a
/// cascade relationship.
/// </summary>
public class AppOperation
{
    public int Id { get; set; }

    public int TrackedAppId { get; set; }

    public AppOperationKind Kind { get; set; }

    public AppOperationStatus Status { get; set; }

    public DateTime StartedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>Docker image tag produced/consumed by a Move build, if any.</summary>
    public string? ImageTag { get; set; }

    /// <summary>Host the operation targets (move destination / removal host).</summary>
    public string? TargetHost { get; set; }

    /// <summary>Host port the operation targets, 0 when the app publishes none.</summary>
    public int HostPort { get; set; }

    /// <summary>Host the app was on when a Move started; null for Remove.</summary>
    public string? OldHost { get; set; }

    /// <summary>Host port the app had when a Move started; 0 when it had none.</summary>
    public int OldPort { get; set; }

    public string Log { get; set; } = string.Empty;
}
