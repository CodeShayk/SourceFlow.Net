#nullable enable

using Microsoft.EntityFrameworkCore;
using SourceFlow.Stores.EntityFramework.Models;

namespace SourceFlow.Stores.EntityFramework;

/// <summary>
/// DbContext for idempotency tracking
/// </summary>
public class IdempotencyDbContext : DbContext
{
    public IdempotencyDbContext(DbContextOptions<IdempotencyDbContext> options)
        : base(options)
    {
    }

    public DbSet<IdempotencyRecord> IdempotencyRecords { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<IdempotencyRecord>(entity =>
        {
            entity.ToTable("IdempotencyRecords");
            
            entity.HasKey(e => e.IdempotencyKey);
            
            entity.Property(e => e.IdempotencyKey)
                .IsRequired()
                .HasMaxLength(500);
            
            entity.Property(e => e.ProcessedAt)
                .IsRequired();
            
            entity.Property(e => e.ExpiresAt)
                .IsRequired();
            
            entity.Property(e => e.MessageType)
                .HasMaxLength(500);
            
            entity.Property(e => e.CloudProvider)
                .HasMaxLength(50);

            // Index for efficient expiration cleanup
            entity.HasIndex(e => e.ExpiresAt)
                .HasDatabaseName("IX_IdempotencyRecords_ExpiresAt");
        });
    }
}
