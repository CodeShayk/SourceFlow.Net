#nullable enable

using System;
using Microsoft.EntityFrameworkCore;
using SourceFlow.Stores.EntityFramework.Models;
using SourceFlow.Stores.EntityFramework.Options;

namespace SourceFlow.Stores.EntityFramework
{
    /// <summary>
    /// DbContext specifically for command storage
    /// </summary>
    public class CommandDbContext : DbContext
    {
        private static TableNamingConvention? _namingConvention;

        public CommandDbContext(DbContextOptions<CommandDbContext> options) : base(options)
        {
        }

        /// <summary>
        /// Configure the table naming convention for command tables.
        /// </summary>
        /// <param name="convention">The naming convention to use</param>
        public static void ConfigureNamingConvention(TableNamingConvention? convention)
        {
            _namingConvention = convention;
        }

        public DbSet<CommandRecord> Commands { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ApplyConfiguration(new CommandRecordConfiguration());

            // Apply naming convention to CommandRecord table if configured
            if (_namingConvention != null)
            {
                var tableName = _namingConvention.ApplyConvention(nameof(CommandRecord));
                var entity = modelBuilder.Entity<CommandRecord>();

                if (_namingConvention.UseSchema && !string.IsNullOrEmpty(_namingConvention.SchemaName))
                {
                    entity.ToTable(tableName, _namingConvention.SchemaName);
                }
                else
                {
                    entity.ToTable(tableName);
                }
            }
        }
    }
}