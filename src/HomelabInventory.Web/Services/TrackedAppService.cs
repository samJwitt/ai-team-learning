using HomelabInventory.Web.Data;
using HomelabInventory.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace HomelabInventory.Web.Services;

/// <summary>
/// Outcome of a create/update/delete attempt. On failure, <see cref="Errors"/>
/// holds human-readable messages for the form to surface; nothing was saved.
/// </summary>
public sealed record TrackedAppResult(
    bool Success,
    TrackedApp? App,
    IReadOnlyList<string> Errors)
{
    private static readonly IReadOnlyList<string> NoErrors = [];

    public static TrackedAppResult Ok(TrackedApp app) => new(true, app, NoErrors);

    public static TrackedAppResult Fail(params string[] errors) =>
        new(false, null, errors);

    public static TrackedAppResult Invalid(TrackedApp app, IReadOnlyList<string> fieldErrors) =>
        app is null
            ? Fail("No app data was submitted.")
            : new(false, null, fieldErrors);
}

/// <summary>
/// The single server-side authority for TrackedApp CRUD and validation
/// (acceptance criterion 4). Blazor forms call into this and surface the
/// returned errors instead of saving.
/// </summary>
public sealed class TrackedAppService(HomelabDbContext db)
{
    public async Task<List<TrackedApp>> ListAsync(CancellationToken ct = default) =>
        await db.TrackedApps.OrderBy(a => a.NormalizedName).ToListAsync(ct);

    public Task<TrackedApp?> GetAsync(int id, CancellationToken ct = default) =>
        db.TrackedApps.FirstOrDefaultAsync(a => a.Id == id, ct);

    public async Task<TrackedAppResult> CreateAsync(TrackedApp app, CancellationToken ct = default)
    {
        var fieldErrors = ValidateFields(app);
        if (fieldErrors.Count > 0)
        {
            return TrackedAppResult.Invalid(app, fieldErrors);
        }

        Normalize(app);

        if (await NameTakenAsync(app.NormalizedName, excludedId: null, ct))
        {
            return TrackedAppResult.Fail($"An app named '{app.Name}' already exists.");
        }

        db.TrackedApps.Add(app);
        await db.SaveChangesAsync(ct);
        return TrackedAppResult.Ok(app);
    }

    public async Task<TrackedAppResult> UpdateAsync(TrackedApp app, CancellationToken ct = default)
    {
        var fieldErrors = ValidateFields(app);
        if (fieldErrors.Count > 0)
        {
            return TrackedAppResult.Invalid(app, fieldErrors);
        }

        Normalize(app);

        var existing = await db.TrackedApps.FirstOrDefaultAsync(a => a.Id == app.Id, ct);
        if (existing is null)
        {
            return TrackedAppResult.Fail($"No app with id {app.Id}.");
        }

        if (await NameTakenAsync(app.NormalizedName, excludedId: app.Id, ct))
        {
            return TrackedAppResult.Fail($"An app named '{app.Name}' already exists.");
        }

        existing.Name = app.Name;
        existing.NormalizedName = app.NormalizedName;
        existing.Description = app.Description;
        existing.HostnameOrIp = app.HostnameOrIp;
        existing.Port = app.Port;
        existing.Url = app.Url;
        existing.DeployedOn = app.DeployedOn;
        existing.ServedVia = app.ServedVia;
        existing.ProxyPath = app.ProxyPath;
        existing.RepoUrl = app.RepoUrl;
        existing.Branch = app.Branch;
        existing.ContainerImage = app.ContainerImage;
        await db.SaveChangesAsync(ct);
        return TrackedAppResult.Ok(existing);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        var existing = await db.TrackedApps.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (existing is null)
        {
            return false;
        }

        db.TrackedApps.Remove(existing);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<bool> NameTakenAsync(string normalizedName, int? excludedId, CancellationToken ct) =>
        await db.TrackedApps.AnyAsync(
            a => a.NormalizedName == normalizedName && (excludedId == null || a.Id != excludedId), ct);

    /// <summary>
    /// Field-level rules that don't need the database. The service is the
    /// source of truth: UI markup never substitutes for these checks.
    /// </summary>
    public static IReadOnlyList<string> ValidateFields(TrackedApp app)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(app.Name))
        {
            errors.Add("Name is required.");
        }

        if (string.IsNullOrWhiteSpace(app.HostnameOrIp))
        {
            errors.Add("Host or IP is required.");
        }

        if (app.Port is < 1 or > 65535)
        {
            errors.Add("Port must be between 1 and 65535.");
        }

        if (!IsHttpUrl(app.Url))
        {
            errors.Add("URL must be an absolute http(s) address when set.");
        }

        if (!IsHttpUrl(app.RepoUrl))
        {
            errors.Add("Repository URL must be an absolute http(s) address when set.");
        }

        return errors;
    }

    private static bool IsHttpUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true; // optional fields are valid when empty
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static void Normalize(TrackedApp app)
    {
        app.Name = app.Name.Trim();
        app.NormalizedName = app.Name.ToLowerInvariant();
        app.HostnameOrIp = app.HostnameOrIp.Trim();
        if (string.IsNullOrWhiteSpace(app.Branch))
        {
            app.Branch = "main";
        }
    }
}
