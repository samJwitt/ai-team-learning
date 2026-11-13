using System.Text;

namespace HomelabInventory.Web.Services.Update;

/// <summary>
/// Caps the persisted <see cref="Models.UpdateRun.Log"/> so a chatty build
/// (or a hostile repo whose build spews) can't grow the SQLite row without
/// bound. Keeps the first and last halves of the budget — the head carries
/// the command context, the tail carries the failure — and replaces the
/// middle with an elision marker.
/// </summary>
public static class UpdateRunLog
{
    /// <summary>Hard cap on the persisted log, in characters (~64 KB).</summary>
    public const int MaxChars = 64 * 1024;

    // Reserved so the marker fits even for huge elision counts, keeping the
    // final string within MaxChars without a second pass.
    private const int MarkerBudget = 64;

    public static string Cap(string log)
    {
        if (log.Length <= MaxChars)
        {
            return log;
        }

        var keep = MaxChars - MarkerBudget;
        var head = log[..(keep / 2)];
        var tail = log[^((keep + 1) / 2)..];
        var elided = log.Length - keep;

        var sb = new StringBuilder(MaxChars);
        sb.Append(head);
        sb.Append("\n\n... [").Append(elided).Append(" characters elided] ...\n\n");
        sb.Append(tail);
        return sb.ToString();
    }
}
