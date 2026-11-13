using System.Text.RegularExpressions;

namespace HomelabInventory.Web.Services.Update;

/// <summary>
/// Allowlist validator for host and docker object names that end up inside
/// ssh/docker argv or remote shell command strings. Only characters that can
/// never form a shell metacharacter, an option prefix (first char must be
/// alphanumeric), or a quoting hazard pass. This is the single definition used
/// by every deploy-target command builder — do not fork a per-adapter regex.
/// </summary>
public static partial class DeployToken
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:@/-]*$")]
    public static partial Regex Safe();

    public static bool IsValid(string? token) => token is not null && Safe().IsMatch(token);
}
