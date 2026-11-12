using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace HomelabInventory.Tests;

/// <summary>
/// Hosts the real app against a dedicated SQLite file so tests exercise the
/// same EF Core + SQLite persistence the app uses, and so one factory can be
/// torn down and a second pointed at the same file (restart durability).
/// </summary>
public sealed class HomelabWebAppFactory(string databasePath) : WebApplicationFactory<Program>
{
    public string DatabasePath { get; } = databasePath;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Database:Path", DatabasePath);
    }
}
