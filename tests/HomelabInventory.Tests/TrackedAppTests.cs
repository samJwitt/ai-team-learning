using System.Net;
using HomelabInventory.Web.Models;
using HomelabInventory.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace HomelabInventory.Tests;

/// <summary>
/// Integration tests for the tracked-app registry: the app runs via
/// WebApplicationFactory against a temp SQLite file. Reads are asserted
/// through real HTTP (prerendered pages); writes go through the same
/// TrackedAppService the interactive forms call, since Blazor Server form
/// posts require a live SignalR circuit that plain HTTP cannot drive.
/// </summary>
public sealed class TrackedAppTests : IDisposable
{
    private readonly string dbPath =
        Path.Combine(Path.GetTempPath(), $"homelab-tests-{Guid.NewGuid():N}.db");

    private WebApplicationFactory<Program> CreateFactory() => new HomelabWebAppFactory(dbPath);

    public void Dispose()
    {
        foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(dbPath) + "*"))
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    private async Task<TrackedApp> CreateAppAsync(
        WebApplicationFactory<Program> factory,
        string name,
        Action<TrackedApp>? mutate = null)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TrackedAppService>();

        var app = new TrackedApp { Name = name, HostnameOrIp = "nas.local" };
        mutate?.Invoke(app);

        var result = await service.CreateAsync(app);
        Assert.True(result.Success, "create failed: " + string.Join("; ", result.Errors));
        return result.App!;
    }

    private async Task<TrackedAppResult> TryCreateAppAsync(
        WebApplicationFactory<Program> factory,
        string name,
        Action<TrackedApp>? mutate = null)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TrackedAppService>();

        var app = new TrackedApp { Name = name, HostnameOrIp = "nas.local" };
        mutate?.Invoke(app);
        return await service.CreateAsync(app);
    }

    [Fact]
    public async Task HomePage_LinksToApps()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var html = await client.GetStringAsync("/");

        Assert.Contains("href=\"/apps\"", html);
    }

    [Fact]
    public async Task CreateApp_AppearsInList_AndOnDetailPage()
    {
        await using var factory = CreateFactory();
        var created = await CreateAppAsync(factory, "Grafana", a =>
        {
            a.Description = "Metrics dashboards";
            a.Port = 3000;
            a.ServedVia = ServedVia.Container;
        });

        var client = factory.CreateClient();

        var listHtml = await client.GetStringAsync("/apps");
        Assert.Contains("Grafana", listHtml);
        Assert.Contains("Add app", listHtml);

        var detailHtml = await client.GetStringAsync($"/apps/{created.Id}");
        Assert.Contains("Grafana", detailHtml);
        Assert.Contains("Metrics dashboards", detailHtml);
        Assert.Contains("3000", detailHtml);
    }

    [Fact]
    public async Task EditApp_ChangesPersist()
    {
        await using var factory = CreateFactory();
        var created = await CreateAppAsync(factory, "Home Assistant");

        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<TrackedAppService>();
            var loaded = await service.GetAsync(created.Id);
            Assert.NotNull(loaded);
            loaded.Name = "Home Assistant OS";
            loaded.Description = "Now with add-ons";

            var result = await service.UpdateAsync(loaded);
            Assert.True(result.Success, string.Join("; ", result.Errors));
        }

        var client = factory.CreateClient();
        var detailHtml = await client.GetStringAsync($"/apps/{created.Id}");
        Assert.Contains("Home Assistant OS", detailHtml);
        Assert.Contains("Now with add-ons", detailHtml);

        var listHtml = await client.GetStringAsync("/apps");
        Assert.Contains("Home Assistant OS", listHtml);
    }

    [Fact]
    public async Task DeleteApp_RemovesIt()
    {
        await using var factory = CreateFactory();
        var created = await CreateAppAsync(factory, "Runner");

        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<TrackedAppService>();
            Assert.True(await service.DeleteAsync(created.Id));
        }

        var client = factory.CreateClient();

        var listHtml = await client.GetStringAsync("/apps");
        Assert.DoesNotContain("Runner</a>", listHtml);

        var detailResponse = await client.GetAsync($"/apps/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, detailResponse.StatusCode);
    }

    [Fact]
    public async Task CreateApp_WithDuplicateName_IgnoringCase_IsRejected()
    {
        await using var factory = CreateFactory();
        await CreateAppAsync(factory, "Paperless");

        var duplicate = await TryCreateAppAsync(factory, "PAPERLESS");

        Assert.False(duplicate.Success);
        Assert.Contains(duplicate.Errors, e => e.Contains("already exists"));

        var listHtml = await factory.CreateClient().GetStringAsync("/apps");
        Assert.Contains("Paperless", listHtml);
        // Exactly one record: the rejected create did not save.
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TrackedAppService>();
        Assert.Single(await service.ListAsync());
    }

    [Fact]
    public async Task CreateApp_WithInvalidPort_IsRejected()
    {
        await using var factory = CreateFactory();

        var tooHigh = await TryCreateAppAsync(factory, "BadPort", a => a.Port = 70000);
        Assert.False(tooHigh.Success);
        Assert.Contains(tooHigh.Errors, e => e.Contains("Port"));

        var zero = await TryCreateAppAsync(factory, "ZeroPort", a => a.Port = 0);
        Assert.False(zero.Success);

        // Boundary values are accepted.
        var ok = await TryCreateAppAsync(factory, "FinePort", a => a.Port = 8080);
        Assert.True(ok.Success, string.Join("; ", ok.Errors));
    }

    [Fact]
    public async Task CreateApp_WithEmptyNameOrBadUrls_IsRejected()
    {
        await using var factory = CreateFactory();

        var emptyName = await TryCreateAppAsync(factory, "   ");
        Assert.False(emptyName.Success);
        Assert.Contains(emptyName.Errors, e => e.Contains("Name is required"));

        var relativeUrl = await TryCreateAppAsync(factory, "RelativeUrl", a => a.Url = "example.com/app");
        Assert.False(relativeUrl.Success);

        var ftpUrl = await TryCreateAppAsync(factory, "FtpRepo", a => a.RepoUrl = "ftp://example.com/repo");
        Assert.False(ftpUrl.Success);

        var listHtml = await factory.CreateClient().GetStringAsync("/apps");
        Assert.DoesNotContain("RelativeUrl", listHtml);
        Assert.DoesNotContain("FtpRepo", listHtml);
    }

    /// <summary>
    /// Regression (gate review on PR #2): in Blazor Server the scoped
    /// DbContext/TrackedAppService lives for the whole circuit, so if reads
    /// return change-tracked entities, edits abandoned in the form (never
    /// submitted, never validated) are flushed by the NEXT SaveChangesAsync
    /// of any operation in the same scope — bypassing server-side validation.
    /// Reads must be AsNoTracking so an abandoned draft cannot leak.
    /// </summary>
    [Fact]
    public async Task AbandonedEdit_DoesNotLeakIntoNextCreate_InSameScope()
    {
        await using var factory = CreateFactory();
        var app1 = await CreateAppAsync(factory, "Grafana", a => a.Port = 3000);

        // One scope simulates one interactive-server circuit spanning pages.
        using (var circuit = factory.Services.CreateScope())
        {
            var service = circuit.ServiceProvider.GetRequiredService<TrackedAppService>();

            // AppEdit binds its draft to the GetAsync result; the user types
            // values that would fail validation, then navigates away without saving.
            var draft = await service.GetAsync(app1.Id);
            Assert.NotNull(draft);
            draft.Name = "Abandoned Junk";
            draft.Port = 70000; // invalid: must never reach the DB

            // A different page in the same circuit successfully creates an app.
            var created = await service.CreateAsync(
                new TrackedApp { Name = "Jellyfin", HostnameOrIp = "nas.local" });
            Assert.True(created.Success, string.Join("; ", created.Errors));
        }

        // Fresh scope reads the committed state of the database.
        using var verify = factory.Services.CreateScope();
        var verifyService = verify.ServiceProvider.GetRequiredService<TrackedAppService>();

        var reloaded = await verifyService.GetAsync(app1.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("Grafana", reloaded.Name);
        Assert.Equal(3000, reloaded.Port);

        var html = await factory.CreateClient().GetStringAsync($"/apps/{app1.Id}");
        Assert.DoesNotContain("Abandoned Junk", html);
    }

    [Fact]
    public async Task AbandonedEdit_DoesNotLeakIntoLaterUpdate_InSameScope()
    {
        await using var factory = CreateFactory();
        var app1 = await CreateAppAsync(factory, "Gitea", a => a.Port = 3001);
        var app2 = await CreateAppAsync(factory, "Uptime Kuma", a => a.Port = 3002);

        using (var circuit = factory.Services.CreateScope())
        {
            var service = circuit.ServiceProvider.GetRequiredService<TrackedAppService>();

            // Abandon an invalid edit on app1.
            var abandoned = await service.GetAsync(app1.Id);
            Assert.NotNull(abandoned);
            abandoned.Port = 99999;
            abandoned.Url = "not-a-valid-url";

            // A detached draft of app2 is updated legitimately (the shape
            // AppEdit uses after the AsNoTracking fix).
            var draft2 = await service.GetAsync(app2.Id);
            Assert.NotNull(draft2);
            draft2.Description = "legit edit";
            var result = await service.UpdateAsync(draft2);
            Assert.True(result.Success, string.Join("; ", result.Errors));
        }

        using var verify = factory.Services.CreateScope();
        var verifyService = verify.ServiceProvider.GetRequiredService<TrackedAppService>();

        var reloaded1 = await verifyService.GetAsync(app1.Id);
        Assert.NotNull(reloaded1);
        Assert.Equal(3001, reloaded1.Port);
        Assert.Null(reloaded1.Url);

        // The legitimate update must still work with detached drafts.
        var reloaded2 = await verifyService.GetAsync(app2.Id);
        Assert.NotNull(reloaded2);
        Assert.Equal("legit edit", reloaded2.Description);
    }

    [Fact]
    public async Task DataWrittenByOneHost_IsReadableByASecondHostOnSameSqliteFile()
    {
        // First app instance writes and shuts down.
        int createdId;
        await using (var writer = CreateFactory())
        {
            var created = await CreateAppAsync(writer, "Vaultwarden");
            createdId = created.Id;
        } // host torn down — simulates app shutdown

        // Second app instance, pointed at the same SQLite file.
        await using var reader = CreateFactory();
        var html = await reader.CreateClient().GetStringAsync("/apps");
        Assert.Contains("Vaultwarden", html);

        var detailHtml = await reader.CreateClient().GetStringAsync($"/apps/{createdId}");
        Assert.Contains("Vaultwarden", detailHtml);
    }
}
