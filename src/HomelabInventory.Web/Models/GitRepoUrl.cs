namespace HomelabInventory.Web.Models;

/// <summary>
/// Single source of truth for which repository URLs may ever reach the
/// <c>git</c> CLI. Only plain transport schemes are allowed; everything else
/// — <c>ext::sh ...</c>, <c>file://</c>, scp-style <c>user@host:path</c>, and
/// anything beginning with <c>-</c> (the <c>--upload-pack=</c> argv-injection
/// class) — is refused before a process is spawned.
/// </summary>
public static class GitRepoUrl
{
    private static readonly string[] AllowedSchemes =
    [
        "https://",
        "http://",
        "git://",
        "ssh://",
    ];

    /// <summary>
    /// True when <paramref name="repoUrl"/> is null/blank (the field is
    /// optional) or starts with one of the four allowed transport schemes
    /// (case-insensitive).
    /// </summary>
    public static bool IsAllowed(string? repoUrl)
    {
        if (string.IsNullOrWhiteSpace(repoUrl))
        {
            return true;
        }

        var trimmed = repoUrl.TrimStart();
        foreach (var scheme in AllowedSchemes)
        {
            if (trimmed.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
