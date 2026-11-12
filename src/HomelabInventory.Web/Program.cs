using HomelabInventory.Web.Components;
using HomelabInventory.Web.Data;
using HomelabInventory.Web.Services;
using HomelabInventory.Web.Services.Update;
using Microsoft.EntityFrameworkCore;

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
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HomelabDbContext>();
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
