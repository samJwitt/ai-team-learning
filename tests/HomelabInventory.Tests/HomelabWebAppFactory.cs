using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace HomelabInventory.Tests;

/// <summary>
/// Hosts the real app against a dedicated SQLite file so tests exercise the
/// same EF Core + SQLite persistence the app uses, and so one factory can be
/// torn down and a second pointed at the same file (restart durability).
/// </summary>
public class HomelabWebAppFactory(string databasePath) : WebApplicationFactory<Program>
{
    public string DatabasePath { get; } = databasePath;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Database:Path", DatabasePath);
        builder.ConfigureTestServices(ConfigureTestServices);
    }

    /// <summary>
    /// Seam for subclasses to swap app services (e.g. the update pipeline's
    /// fakes) after the app's own registrations have run.
    /// </summary>
    protected virtual void ConfigureTestServices(IServiceCollection services)
    {
    }
}
