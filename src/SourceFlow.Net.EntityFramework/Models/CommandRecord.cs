using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SourceFlow.Messaging.Commands;

namespace SourceFlow.Stores.EntityFramework.Models
{
    public class CommandRecord
    {
        public long Id { get; set; }
        public int EntityId { get; set; }
        public int SequenceNo { get; set; }
        public string CommandName { get; set; } = string.Empty;
        public string CommandType { get; set; } = string.Empty;
        
        // Store command data in relational fields instead of serialization
        public string PayloadType { get; set; } = string.Empty;
        public string PayloadData { get; set; } = string.Empty; // This can be JSON but for the payload itself
        
        public string Metadata { get; set; } = string.Empty; // Store metadata as JSON
        public DateTime Timestamp { get; set; }
        
        // Relational fields that can be indexed and queried
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    public class CommandRecordConfiguration : IEntityTypeConfiguration<CommandRecord>
    {
        public void Configure(EntityTypeBuilder<CommandRecord> builder)
        {
            builder.HasKey(c => c.Id);
            builder.HasIndex(c => new { c.EntityId, c.SequenceNo }).IsUnique();
            builder.HasIndex(c => c.EntityId);
            builder.HasIndex(c => c.Timestamp);
        }
    }
}