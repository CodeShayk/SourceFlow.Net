using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Text.Json;
using System.Threading.Tasks;
using SourceFlow.Messaging.Commands;
using SourceFlow.Net.SQL.Configuration;

namespace SourceFlow.Net.SQL.Stores
{
    /// <summary>
    /// SQL Server implementation of ICommandStore using ADO.NET.
    /// </summary>
    public class SqlCommandStore : ICommandStore
    {
        private readonly string _connectionString;
        private readonly string _schema;
        private readonly int _commandTimeout;
        private readonly JsonSerializerOptions _jsonOptions;

        /// <summary>
        /// Initializes a new instance of SqlCommandStore.
        /// </summary>
        /// <param name="configuration">SQL store configuration</param>
        public SqlCommandStore(SqlStoreConfiguration configuration)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));

            configuration.Validate();

            _connectionString = configuration.CommandStoreConnectionString;
            _schema = configuration.CommandStoreSchema;
            _commandTimeout = configuration.CommandTimeout;

            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = false
            };
        }

        /// <summary>
        /// Appends a command to the store.
        /// </summary>
        public async Task Append(ICommand command)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            const string sql = @"
                INSERT INTO [{0}].[Commands]
                (EntityId, SequenceNo, CommandName, CommandType, Payload, Metadata, Timestamp)
                VALUES
                (@EntityId, @SequenceNo, @CommandName, @CommandType, @Payload, @Metadata, @Timestamp)";

            var formattedSql = string.Format(sql, _schema);

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                using (var cmd = new SqlCommand(formattedSql, connection))
                {
                    cmd.CommandTimeout = _commandTimeout;
                    cmd.Parameters.AddWithValue("@EntityId", command.Entity.Id);
                    cmd.Parameters.AddWithValue("@SequenceNo", command.Metadata.SequenceNo);
                    cmd.Parameters.AddWithValue("@CommandName", command.Name ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@CommandType", command.GetType().AssemblyQualifiedName);
                    cmd.Parameters.AddWithValue("@Payload", JsonSerializer.Serialize(command.Payload, _jsonOptions));
                    cmd.Parameters.AddWithValue("@Metadata", JsonSerializer.Serialize(command.Metadata, _jsonOptions));
                    cmd.Parameters.AddWithValue("@Timestamp", command.Metadata.OccurredOn);

                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }

        /// <summary>
        /// Loads all commands for a given entity from the store.
        /// </summary>
        public async Task<IEnumerable<ICommand>> Load(int entityId)
        {
            const string sql = @"
                SELECT EntityId, SequenceNo, CommandName, CommandType, Payload, Metadata, Timestamp
                FROM [{0}].[Commands]
                WHERE EntityId = @EntityId
                ORDER BY SequenceNo ASC";

            var formattedSql = string.Format(sql, _schema);
            var commands = new List<ICommand>();

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                using (var cmd = new SqlCommand(formattedSql, connection))
                {
                    cmd.CommandTimeout = _commandTimeout;
                    cmd.Parameters.AddWithValue("@EntityId", entityId);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var commandTypeString = reader.GetString(reader.GetOrdinal("CommandType"));
                            var commandType = Type.GetType(commandTypeString);

                            if (commandType == null)
                            {
                                // Log warning: command type not found
                                continue;
                            }

                            var payloadJson = reader.GetString(reader.GetOrdinal("Payload"));
                            var metadataJson = reader.GetString(reader.GetOrdinal("Metadata"));

                            // This is a simplified deserialization. In production, you'd need more robust type handling
                            // You might want to store payload type separately and deserialize appropriately
                            var command = JsonSerializer.Deserialize(
                                $"{{\"Payload\":{payloadJson},\"Metadata\":{metadataJson}}}",
                                commandType,
                                _jsonOptions
                            ) as ICommand;

                            if (command != null)
                            {
                                commands.Add(command);
                            }
                        }
                    }
                }
            }

            return commands;
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

                IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[{0}].[Commands]') AND type in (N'U'))
                BEGIN
                    CREATE TABLE [{0}].[Commands] (
                        Id BIGINT IDENTITY(1,1) PRIMARY KEY,
                        EntityId INT NOT NULL,
                        SequenceNo INT NOT NULL,
                        CommandName NVARCHAR(500),
                        CommandType NVARCHAR(MAX) NOT NULL,
                        Payload NVARCHAR(MAX) NOT NULL,
                        Metadata NVARCHAR(MAX) NOT NULL,
                        Timestamp DATETIME2 NOT NULL,
                        CreatedAt DATETIME2 DEFAULT GETUTCDATE(),
                        INDEX IX_Commands_EntityId (EntityId),
                        INDEX IX_Commands_EntityId_SequenceNo (EntityId, SequenceNo),
                        UNIQUE (EntityId, SequenceNo)
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
