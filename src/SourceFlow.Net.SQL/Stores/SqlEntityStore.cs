using System;
using System.Data.SqlClient;
using System.Text.Json;
using System.Threading.Tasks;
using SourceFlow.Net.SQL.Configuration;

namespace SourceFlow.Net.SQL.Stores
{
    /// <summary>
    /// SQL Server implementation of IEntityStore using ADO.NET.
    /// </summary>
    public class SqlEntityStore : IEntityStore
    {
        private readonly string _connectionString;
        private readonly string _schema;
        private readonly int _commandTimeout;
        private readonly JsonSerializerOptions _jsonOptions;

        /// <summary>
        /// Initializes a new instance of SqlEntityStore.
        /// </summary>
        /// <param name="configuration">SQL store configuration</param>
        public SqlEntityStore(SqlStoreConfiguration configuration)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));

            configuration.Validate();

            _connectionString = configuration.EntityStoreConnectionString;
            _schema = configuration.EntityStoreSchema;
            _commandTimeout = configuration.CommandTimeout;

            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = false
            };
        }

        /// <summary>
        /// Retrieves an entity by unique identifier.
        /// </summary>
        public async Task<TEntity> Get<TEntity>(int id) where TEntity : class, IEntity
        {
            if (id <= 0)
                throw new ArgumentException("Entity Id must be greater than 0.", nameof(id));

            const string sql = @"
                SELECT EntityData
                FROM [{0}].[Entities]
                WHERE Id = @Id AND EntityType = @EntityType";

            var formattedSql = string.Format(sql, _schema);
            var entityType = typeof(TEntity).AssemblyQualifiedName;

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                using (var cmd = new SqlCommand(formattedSql, connection))
                {
                    cmd.CommandTimeout = _commandTimeout;
                    cmd.Parameters.AddWithValue("@Id", id);
                    cmd.Parameters.AddWithValue("@EntityType", entityType);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            var entityData = reader.GetString(reader.GetOrdinal("EntityData"));
                            return JsonSerializer.Deserialize<TEntity>(entityData, _jsonOptions);
                        }
                    }
                }
            }

            throw new InvalidOperationException($"Entity of type {typeof(TEntity).Name} with Id {id} not found.");
        }

        /// <summary>
        /// Creates or updates an entity to the store.
        /// </summary>
        public async Task Persist<TEntity>(TEntity entity) where TEntity : IEntity
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            if (entity.Id <= 0)
                throw new ArgumentException("Entity Id must be greater than 0.", nameof(entity));

            const string sql = @"
                MERGE [{0}].[Entities] AS target
                USING (SELECT @Id AS Id, @EntityType AS EntityType) AS source
                ON (target.Id = source.Id AND target.EntityType = source.EntityType)
                WHEN MATCHED THEN
                    UPDATE SET
                        EntityData = @EntityData,
                        UpdatedAt = GETUTCDATE()
                WHEN NOT MATCHED THEN
                    INSERT (Id, EntityType, EntityData, CreatedAt, UpdatedAt)
                    VALUES (@Id, @EntityType, @EntityData, GETUTCDATE(), GETUTCDATE());";

            var formattedSql = string.Format(sql, _schema);
            var entityType = typeof(TEntity).AssemblyQualifiedName;
            var entityData = JsonSerializer.Serialize(entity, _jsonOptions);

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                using (var cmd = new SqlCommand(formattedSql, connection))
                {
                    cmd.CommandTimeout = _commandTimeout;
                    cmd.Parameters.AddWithValue("@Id", entity.Id);
                    cmd.Parameters.AddWithValue("@EntityType", entityType);
                    cmd.Parameters.AddWithValue("@EntityData", entityData);

                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }

        /// <summary>
        /// Deletes an entity from the store.
        /// </summary>
        public async Task Delete<TEntity>(TEntity entity) where TEntity : IEntity
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            if (entity.Id <= 0)
                throw new ArgumentException("Entity Id must be greater than 0.", nameof(entity));

            const string sql = @"
                DELETE FROM [{0}].[Entities]
                WHERE Id = @Id AND EntityType = @EntityType";

            var formattedSql = string.Format(sql, _schema);
            var entityType = typeof(TEntity).AssemblyQualifiedName;

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                using (var cmd = new SqlCommand(formattedSql, connection))
                {
                    cmd.CommandTimeout = _commandTimeout;
                    cmd.Parameters.AddWithValue("@Id", entity.Id);
                    cmd.Parameters.AddWithValue("@EntityType", entityType);

                    var rowsAffected = await cmd.ExecuteNonQueryAsync();

                    if (rowsAffected == 0)
                    {
                        throw new InvalidOperationException(
                            $"Entity of type {typeof(TEntity).Name} with Id {entity.Id} not found.");
                    }
                }
            }
        }

        /// <summary>
        /// Creates the required database table schema.
        /// Call this during application startup or migration.
        /// </summary>
        public async Task EnsureSchemaCreated()
        {
            const string sql = @"
                IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = '{0}')
                BEGIN
                    EXEC('CREATE SCHEMA [{0}]')
                END

                IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[{0}].[Entities]') AND type in (N'U'))
                BEGIN
                    CREATE TABLE [{0}].[Entities] (
                        Id INT NOT NULL,
                        EntityType NVARCHAR(500) NOT NULL,
                        EntityData NVARCHAR(MAX) NOT NULL,
                        CreatedAt DATETIME2 DEFAULT GETUTCDATE(),
                        UpdatedAt DATETIME2 DEFAULT GETUTCDATE(),
                        PRIMARY KEY (Id, EntityType),
                        INDEX IX_Entities_EntityType (EntityType)
                    )
                END";

            var formattedSql = string.Format(sql, _schema);

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                using (var cmd = new SqlCommand(formattedSql, connection))
                {
                    cmd.CommandTimeout = _commandTimeout;
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
    }
}
