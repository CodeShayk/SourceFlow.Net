using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace SourceFlow.Stores.EntityFramework.Migrations
{
    /// <summary>
    /// Helper class to manually create database schemas for dynamic entity and view model types.
    /// This bypasses EF Core's model caching to support runtime type registration.
    /// </summary>
    public static class DbContextMigrationHelper
    {
        /// <summary>
        /// Manually creates tables for all registered IEntity types in the EntityDbContext.
        /// </summary>
        public static void CreateEntityTables(EntityDbContext context, IEnumerable<Type> entityTypes)
        {
            var databaseCreator = context.Database.GetService<IRelationalDatabaseCreator>();

            // Ensure database exists
            context.Database.EnsureCreated();

            foreach (var entityType in entityTypes)
            {
                var tableName = entityType.Name;
                var properties = entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanRead && p.CanWrite);

                var columns = new List<string>();
                foreach (var prop in properties)
                {
                    var columnDef = GetColumnDefinition(prop);
                    if (columnDef != null)
                    {
                        columns.Add(columnDef);
                    }
                }

                if (columns.Any())
                {
                    var createTableSql = $@"
                        CREATE TABLE IF NOT EXISTS ""{tableName}"" (
                            {string.Join(",\n                            ", columns)},
                            PRIMARY KEY (""Id"")
                        )";

                    try
                    {
                        context.Database.ExecuteSqlRaw(createTableSql);
                    }
                    catch
                    {
                        // Table might already exist, ignore
                    }
                }
            }
        }

        /// <summary>
        /// Manually creates tables for all registered IViewModel types in the ViewModelDbContext.
        /// </summary>
        public static void CreateViewModelTables(ViewModelDbContext context, IEnumerable<Type> viewModelTypes)
        {
            var databaseCreator = context.Database.GetService<IRelationalDatabaseCreator>();

            // Ensure database exists
            context.Database.EnsureCreated();

            foreach (var viewModelType in viewModelTypes)
            {
                var tableName = viewModelType.Name;
                var properties = viewModelType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanRead && p.CanWrite);

                var columns = new List<string>();
                foreach (var prop in properties)
                {
                    var columnDef = GetColumnDefinition(prop);
                    if (columnDef != null)
                    {
                        columns.Add(columnDef);
                    }
                }

                if (columns.Any())
                {
                    var createTableSql = $@"
                        CREATE TABLE IF NOT EXISTS ""{tableName}"" (
                            {string.Join(",\n                            ", columns)},
                            PRIMARY KEY (""Id"")
                        )";

                    try
                    {
                        context.Database.ExecuteSqlRaw(createTableSql);
                    }
                    catch
                    {
                        // Table might already exist, ignore
                    }
                }
            }
        }

        private static string? GetColumnDefinition(PropertyInfo property)
        {
            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var columnName = property.Name;
            string sqlType;

            if (propertyType == typeof(int))
                sqlType = "INTEGER";
            else if (propertyType == typeof(long))
                sqlType = "BIGINT";
            else if (propertyType == typeof(string))
                sqlType = "TEXT";
            else if (propertyType == typeof(bool))
                sqlType = "INTEGER"; // SQLite uses INTEGER for boolean
            else if (propertyType == typeof(DateTime))
                sqlType = "TEXT"; // SQLite stores DateTime as TEXT
            else if (propertyType == typeof(decimal) || propertyType == typeof(double) || propertyType == typeof(float))
                sqlType = "REAL";
            else if (propertyType == typeof(byte[]))
                sqlType = "BLOB";
            else if (propertyType.IsEnum)
                sqlType = "INTEGER";
            else
                return null; // Skip complex types

            var nullable = Nullable.GetUnderlyingType(property.PropertyType) != null ||
                          (!propertyType.IsValueType && columnName != "Id");
            var nullConstraint = nullable ? "" : " NOT NULL";

            return $@"""{columnName}"" {sqlType}{nullConstraint}";
        }
    }
}
