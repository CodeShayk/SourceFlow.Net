using Microsoft.Data.Sqlite;
using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace SourceFlow.Net.SQL.Tests.Helpers
{
    /// <summary>
    /// SQLite implementation of IEntityStore for integration testing.
    /// </summary>
    public class SqliteEntityStore : IEntityStore
    {
        private readonly SqliteConnection _connection;
        private readonly JsonSerializerOptions _jsonOptions;

        public SqliteEntityStore(SqliteConnection connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = false
            };
        }

        public async Task<TEntity> Get<TEntity>(int id) where TEntity : class, IEntity
        {
            if (id <= 0)
                throw new ArgumentException("Entity Id must be greater than 0.", nameof(id));

            const string sql = @"
                SELECT EntityData
                FROM Entities
                WHERE Id = @Id AND EntityType = @EntityType";

            var entityType = typeof(TEntity).AssemblyQualifiedName;

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@Id", id);
            cmd.Parameters.AddWithValue("@EntityType", entityType);

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var entityData = reader.GetString(reader.GetOrdinal("EntityData"));
                return JsonSerializer.Deserialize<TEntity>(entityData, _jsonOptions);
            }

            throw new InvalidOperationException($"Entity of type {typeof(TEntity).Name} with Id {id} not found.");
        }

        public async Task Persist<TEntity>(TEntity entity) where TEntity : IEntity
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            if (entity.Id <= 0)
                throw new ArgumentException("Entity Id must be greater than 0.", nameof(entity));

            var entityType = typeof(TEntity).AssemblyQualifiedName;
            var entityData = JsonSerializer.Serialize(entity, _jsonOptions);

            // Check if exists
            const string checkSql = "SELECT COUNT(*) FROM Entities WHERE Id = @Id AND EntityType = @EntityType";
            using (var checkCmd = _connection.CreateCommand())
            {
                checkCmd.CommandText = checkSql;
                checkCmd.Parameters.AddWithValue("@Id", entity.Id);
                checkCmd.Parameters.AddWithValue("@EntityType", entityType);
                var exists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync()) > 0;

                if (exists)
                {
                    // Update
                    const string updateSql = @"
                        UPDATE Entities
                        SET EntityData = @EntityData, UpdatedAt = datetime('now')
                        WHERE Id = @Id AND EntityType = @EntityType";

                    using var updateCmd = _connection.CreateCommand();
                    updateCmd.CommandText = updateSql;
                    updateCmd.Parameters.AddWithValue("@Id", entity.Id);
                    updateCmd.Parameters.AddWithValue("@EntityType", entityType);
                    updateCmd.Parameters.AddWithValue("@EntityData", entityData);
                    await updateCmd.ExecuteNonQueryAsync();
                }
                else
                {
                    // Insert
                    const string insertSql = @"
                        INSERT INTO Entities (Id, EntityType, EntityData, CreatedAt, UpdatedAt)
                        VALUES (@Id, @EntityType, @EntityData, datetime('now'), datetime('now'))";

                    using var insertCmd = _connection.CreateCommand();
                    insertCmd.CommandText = insertSql;
                    insertCmd.Parameters.AddWithValue("@Id", entity.Id);
                    insertCmd.Parameters.AddWithValue("@EntityType", entityType);
                    insertCmd.Parameters.AddWithValue("@EntityData", entityData);
                    await insertCmd.ExecuteNonQueryAsync();
                }
            }
        }

        public async Task Delete<TEntity>(TEntity entity) where TEntity : IEntity
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            if (entity.Id <= 0)
                throw new ArgumentException("Entity Id must be greater than 0.", nameof(entity));

            const string sql = @"
                DELETE FROM Entities
                WHERE Id = @Id AND EntityType = @EntityType";

            var entityType = typeof(TEntity).AssemblyQualifiedName;

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
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
