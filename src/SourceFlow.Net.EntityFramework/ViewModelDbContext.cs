#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using SourceFlow.Stores.EntityFramework.Options;

namespace SourceFlow.Stores.EntityFramework
{
    /// <summary>
    /// DbContext specifically for view model storage
    /// </summary>
    public class ViewModelDbContext : DbContext
    {
        private static readonly HashSet<Type> _explicitlyRegisteredTypes = new HashSet<Type>();
        private static readonly HashSet<Assembly> _assembliesToScan = new HashSet<Assembly>();
        private static TableNamingConvention? _namingConvention;

        public ViewModelDbContext(DbContextOptions<ViewModelDbContext> options) : base(options)
        {
        }

        /// <summary>
        /// Configure the table naming convention for view model tables.
        /// </summary>
        /// <param name="convention">The naming convention to use</param>
        public static void ConfigureNamingConvention(TableNamingConvention? convention)
        {
            _namingConvention = convention;
        }

        /// <summary>
        /// Explicitly register a view model type before creating the database.
        /// This ensures the type is included in the model even if auto-discovery doesn't find it.
        /// </summary>
        public static void RegisterViewModelType<TViewModel>() where TViewModel : class
        {
            _explicitlyRegisteredTypes.Add(typeof(TViewModel));
        }

        /// <summary>
        /// Register an assembly to scan for view model types.
        /// </summary>
        public static void RegisterAssembly(Assembly assembly)
        {
            if (assembly != null)
            {
                _assembliesToScan.Add(assembly);
            }
        }

        /// <summary>
        /// Clear all registered types and assemblies. Useful for testing.
        /// </summary>
        public static void ClearRegistrations()
        {
            _explicitlyRegisteredTypes.Clear();
            _assembliesToScan.Clear();
        }

        /// <summary>
        /// Get all registered view model types (both explicit and from assemblies).
        /// </summary>
        public static IEnumerable<Type> GetRegisteredTypes()
        {
            var types = new HashSet<Type>(_explicitlyRegisteredTypes);

            // Add types from registered assemblies
            foreach (var assembly in _assembliesToScan)
            {
                try
                {
                    var assemblyTypes = assembly.GetTypes()
                        .Where(t => t.IsClass && !t.IsAbstract &&
                                   t.GetInterfaces().Any(i => i.Name == "IViewModel"));
                    foreach (var type in assemblyTypes)
                    {
                        types.Add(type);
                    }
                }
                catch { }
            }

            return types;
        }

        /// <summary>
        /// Manually creates database tables for all registered view model types.
        /// This bypasses EF Core's model caching and should be called after EnsureCreated().
        /// </summary>
        public void ApplyMigrations()
        {
            var types = GetRegisteredTypes();
            if (types.Any())
            {
                Migrations.DbContextMigrationHelper.CreateViewModelTables(this, types);
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Start with explicitly registered types
            var viewModelTypes = new HashSet<Type>(_explicitlyRegisteredTypes);

            // Add types from explicitly registered assemblies
            foreach (var assembly in _assembliesToScan)
            {
                try
                {
                    var types = assembly.GetTypes()
                        .Where(t => t.IsClass && !t.IsAbstract &&
                                   t.GetInterfaces().Any(i => i.Name == "IViewModel"));
                    foreach (var type in types)
                    {
                        viewModelTypes.Add(type);
                    }
                }
                catch { }
            }

            // Auto-discover from loaded assemblies (fallback)
            var discoveredTypes = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !a.FullName?.StartsWith("Microsoft.") == true
                           && !a.FullName?.StartsWith("System.") == true
                           && !a.FullName?.StartsWith("netstandard") == true)
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); }
                    catch { return Enumerable.Empty<Type>(); }
                })
                .Where(t => t.IsClass && !t.IsAbstract &&
                           t.GetInterfaces().Any(i => i.Name == "IViewModel"));

            foreach (var type in discoveredTypes)
            {
                viewModelTypes.Add(type);
            }

            // Register all discovered and explicitly registered types
            foreach (var viewModelType in viewModelTypes)
            {
                // Register each view model type with EF Core
                // Configure the entity with a primary key on Id property
                var entity = modelBuilder.Entity(viewModelType);
                entity.HasKey("Id");

                // On naming convention if configured
                if (_namingConvention != null)
                {
                    var tableName = _namingConvention.ApplyConvention(viewModelType.Name);

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
}
