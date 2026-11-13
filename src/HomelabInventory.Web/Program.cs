using HomelabInventory.Web.Components;
using HomelabInventory.Web.Data;
using HomelabInventory.Web.Services;
using HomelabInventory.Web.Services.Update;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

// Baseline identity for pre-migration installs (databases built by the old
// EnsureCreated()). Must match the InitialCreate migration id and the
// ProductVersion annotation in HomelabDbContextModelSnapshot; regenerate this
// pair if the baseline migration is ever replaced.
const string InitialCreateMigrationId = "20261112234138_InitialCreate";
const string InitialCreateProductVersion = "10.0.15";

var builder = WebApplication.CreateBuilder(args);

// SQLite database location: configurable via "Database:Path" in appsettings
// (absolute, or relative to the content root); defaults to App_Data/homelab.db.
var dbPath = builder.Configuration["Database:Path"];
if (string.IsNullOrWhiteSpace(dbPath))
{
    dbPath = Path.Combine("App_Data", "homelab.db");
}
if (!Path.IsPathRooted(dbPath))
{
    dbPath = Path.Combine(builder.Environment.ContentRootPath, dbPath);
}
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

builder.Services.AddDbContext<HomelabDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddScoped<TrackedAppService>();
builder.Services.AddHttpContextAccessor();

// Update pipeline: one real implementation per interface (git CLI, docker
// build, docker-over-ssh). Integration tests replace all three with fakes.
builder.Services.AddSingleton<IGitSource, GitCliSource>();
builder.Services.AddSingleton<IImageBuilder, DockerCliImageBuilder>();
builder.Services.AddSingleton<IDeployer, SshDockerDeployer>();
builder.Services.AddSingleton<UpdatePipelineService>();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Apply EF Core migrations on startup (real migrations replace the old
// EnsureCreated so existing installs gain new tables without data loss).
// Databases created by the old EnsureCreated() have the tables but no
// __EFMigrationsHistory, so Migrate() would replay InitialCreate and crash
// with SqliteException "table \"TrackedApps\" already exists". Detect that
// state and stamp the baseline first; Migrate() then applies only the later
// migrations on top of the existing schema.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HomelabDbContext>();

    if (!db.Database.GetAppliedMigrations().Any())
    {
        var creator = db.GetService<IRelationalDatabaseCreator>();
        if (creator.HasTables())
        {
            db.Database.ExecuteSqlRaw(
                """
                CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                    "ProductVersion" TEXT NOT NULL
                );
                """);
            db.Database.ExecuteSqlRaw(
                """
                INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                VALUES ({0}, {1});
                """,
                InitialCreateMigrationId,
                InitialCreateProductVersion);
        }
    }

    db.Database.Migrate();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// Exposes the auto-generated entry-point class to the integration tests
// (WebApplicationFactory<Program>).
public partial class Program { }
