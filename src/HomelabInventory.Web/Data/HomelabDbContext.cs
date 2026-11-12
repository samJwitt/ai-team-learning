using HomelabInventory.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace HomelabInventory.Web.Data;

public class HomelabDbContext(DbContextOptions<HomelabDbContext> options) : DbContext(options)
{
    public DbSet<TrackedApp> TrackedApps => Set<TrackedApp>();
    public DbSet<UpdateRun> UpdateRuns => Set<UpdateRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TrackedApp>(entity =>
        {
            entity.Property(e => e.Name).IsRequired();
            entity.Property(e => e.NormalizedName).IsRequired();
            // Case-insensitive name uniqueness: TrackedAppService stores the
            // lower-cased name here and the index enforces it at the DB level.
            entity.HasIndex(e => e.NormalizedName).IsUnique();
            entity.Property(e => e.HostnameOrIp).IsRequired();
            entity.Property(e => e.ServedVia).HasConversion<string>();
        });

        modelBuilder.Entity<UpdateRun>(entity =>
        {
            // Store the status as text so SQLite rows stay readable in ad-hoc
            // queries, matching how TrackedApp.ServedVia is persisted.
            entity.Property(e => e.Status).HasConversion<string>();
            entity.Property(e => e.Status).IsRequired();
            entity.HasOne(e => e.TrackedApp)
                .WithMany()
                .HasForeignKey(e => e.TrackedAppId)
                .OnDelete(DeleteBehavior.Cascade);
            // The detail page's only run query is "this app's history, newest first".
            entity.HasIndex(e => new { e.TrackedAppId, e.StartedAtUtc });
        });
    }
}
