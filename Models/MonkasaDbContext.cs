using Microsoft.EntityFrameworkCore;

namespace Monkasa.Models;

public sealed class MonkasaDbContext(DbContextOptions<MonkasaDbContext> options) : DbContext(options)
{
    public DbSet<ThumbnailCacheEntry> ThumbnailCache => Set<ThumbnailCacheEntry>();

    public DbSet<AppStateEntry> AppState => Set<AppStateEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ThumbnailCacheEntry>(entity =>
        {
            entity.ToTable("thumbnail_cache");
            entity.HasKey(x => new { x.FilePath, x.Width, x.Height });
            entity.Property(x => x.FilePath).HasColumnName("file_path");
            entity.Property(x => x.LastWriteUtcTicks).HasColumnName("last_write_utc_ticks");
            entity.Property(x => x.FileLength).HasColumnName("file_length");
            entity.Property(x => x.Width).HasColumnName("width");
            entity.Property(x => x.Height).HasColumnName("height");
            entity.Property(x => x.ImageBytes).HasColumnName("image_bytes");
            entity.Property(x => x.UpdatedUtcTicks).HasColumnName("updated_utc_ticks");
            entity.HasIndex(x => x.UpdatedUtcTicks).HasDatabaseName("idx_thumbnail_cache_updated");
        });

        modelBuilder.Entity<AppStateEntry>(entity =>
        {
            entity.ToTable("app_state");
            entity.HasKey(x => x.StateKey);
            entity.Property(x => x.StateKey).HasColumnName("state_key");
            entity.Property(x => x.StateValue).HasColumnName("state_value");
            entity.Property(x => x.UpdatedUtcTicks).HasColumnName("updated_utc_ticks");
        });
    }
}
