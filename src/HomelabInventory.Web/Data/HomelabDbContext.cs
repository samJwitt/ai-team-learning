using HomelabInventory.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace HomelabInventory.Web.Data;

public class HomelabDbContext(DbContextOptions<HomelabDbContext> options) : DbContext(options)
{
    public DbSet<TrackedApp> TrackedApps => Set<TrackedApp>();

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
    }
}
