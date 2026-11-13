using HomelabInventory.Web.Services.Apps;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HomelabInventory.Tests.Data;

/// <summary>
/// Regression test for the PR #3 review blocker: an install whose database was
/// created by the old <c>EnsureCreated()</c> has the tables but no
/// <c>__EFMigrationsHistory</c>, so a bare <c>Migrate()</c> replays
/// InitialCreate and crashes with "table TrackedApps already exists" (exit 134).
/// Startup must instead stamp the baseline and apply only AddUpdateRun.
/// </summary>
public sealed class LegacyBootMigrationTests : IDisposable
{
    private readonly string dbPath =
        Path.Combine(Path.GetTempPath(), $"homelab-legacy-boot-{Guid.NewGuid():N}.db");

    /// <summary>
    /// Exactly the DDL <c>EnsureCreated()</c> produced on main before real
    /// migrations existed (InitialCreate's Up without the history table).
    /// </summary>
    private const string LegacyDdl = """
        CREATE TABLE "TrackedApps" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_TrackedApps" PRIMARY KEY AUTOINCREMENT,
            "Name" TEXT NOT NULL,
            "NormalizedName" TEXT NOT NULL,
            "Description" TEXT NULL,
            "HostnameOrIp" TEXT NOT NULL,
            "Port" INTEGER NULL,
            "Url" TEXT NULL,
            "DeployedOn" TEXT NULL,
            "ServedVia" TEXT NOT NULL,
            "ProxyPath" TEXT NULL,
            "RepoUrl" TEXT NULL,
            "Branch" TEXT NULL,
            "ContainerImage" TEXT NULL
        );
        CREATE UNIQUE INDEX "IX_TrackedApps_NormalizedName" ON "TrackedApps" ("NormalizedName");
        """;

    /// <summary>
    /// Build the legacy database by hand: main-era DDL plus one seeded row,
    /// and deliberately NO __EFMigrationsHistory table.
    /// </summary>
    private void SeedLegacyDatabase()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        using (var ddl = conn.CreateCommand())
        {
            ddl.CommandText = LegacyDdl;
            ddl.ExecuteNonQuery();
        }

        using var seed = conn.CreateCommand();
        seed.CommandText = """
            INSERT INTO "TrackedApps"
                ("Name", "NormalizedName", "Description", "HostnameOrIp", "Port",
                 "Url", "DeployedOn", "ServedVia", "ProxyPath", "RepoUrl", "Branch", "ContainerImage")
            VALUES
                ('Legacy Grafana', 'legacy grafana', 'seeded before migrations',
                 '192.168.1.50', 3000, NULL, '192.168.1.50', 'Container',
                 NULL, NULL, 'main', NULL);
            """;
        seed.ExecuteNonQuery();
    }

    [Fact]
    public async Task Boot_LegacyEnsureCreatedDatabase_StampsBaselineAndKeepsSeededRow()
    {
        SeedLegacyDatabase();

        using var factory = new HomelabWebAppFactory(dbPath);

        // Force the full host startup (which runs the migration block) and
        // prove the app actually serves pages against the migrated file.
        // Before the fix this throws SqliteException: table "TrackedApps" already exists.
        var client = factory.CreateClient();
        var home = await client.GetAsync("/");
        Assert.True(home.IsSuccessStatusCode, $"Home returned {(int)home.StatusCode}");

        await using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            await conn.OpenAsync();

            // AddUpdateRun was applied on top of the stamped baseline.
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='UpdateRuns'";
                Assert.Equal(1L, Convert.ToInt64(await cmd.ExecuteScalarAsync()));
            }

            // The history table now contains both migrations: InitialCreate
            // (stamped) and AddUpdateRun (applied by Migrate).
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    """SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" """;
                var ids = new List<string>();
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    ids.Add(reader.GetString(0));
                }

                Assert.Contains("20261112234138_InitialCreate", ids);
                Assert.Contains("20261112234145_AddUpdateRun", ids);
            }

            // The seeded inventory row survived the baseline stamp + migration.
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    """SELECT COUNT(*) FROM "TrackedApps" WHERE "Name" = 'Legacy Grafana' """;
                Assert.Equal(1L, Convert.ToInt64(await cmd.ExecuteScalarAsync()));
            }
        }

        // The app itself reads the legacy row back through its own service.
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TrackedAppService>();
        var apps = await service.ListAsync();
        var survivor = Assert.Single(apps);
        Assert.Equal("Legacy Grafana", survivor.Name);
        Assert.Equal("192.168.1.50", survivor.HostnameOrIp);
    }

    public void Dispose()
    {
        foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(dbPath) + "*"))
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }
}
