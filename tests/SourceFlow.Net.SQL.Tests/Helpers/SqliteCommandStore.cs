using Microsoft.Data.Sqlite;
using SourceFlow.Messaging.Commands;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace SourceFlow.Net.SQL.Tests.Helpers
{
    /// <summary>
    /// SQLite implementation of ICommandStore for integration testing.
    /// </summary>
    public class SqliteCommandStore : ICommandStore
    {
        private readonly SqliteConnection _connection;
        private readonly JsonSerializerOptions _jsonOptions;

        public SqliteCommandStore(SqliteConnection connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = false
            };
        }

        public async Task Append(ICommand command)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            const string sql = @"
                INSERT INTO Commands (EntityId, SequenceNo, CommandName, CommandType, Payload, Metadata, Timestamp)
                VALUES (@EntityId, @SequenceNo, @CommandName, @CommandType, @Payload, @Metadata, @Timestamp)";

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@EntityId", command.Entity.Id);
            cmd.Parameters.AddWithValue("@SequenceNo", command.Metadata.SequenceNo);
            cmd.Parameters.AddWithValue("@CommandName", command.Name ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@CommandType", command.GetType().AssemblyQualifiedName);
            // Serialize the entire command as JSON for easier deserialization
            cmd.Parameters.AddWithValue("@Payload", JsonSerializer.Serialize(command, command.GetType(), _jsonOptions));
            cmd.Parameters.AddWithValue("@Metadata", JsonSerializer.Serialize(command.Metadata, _jsonOptions));
            cmd.Parameters.AddWithValue("@Timestamp", command.Metadata.OccurredOn.ToString("O"));

            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<IEnumerable<ICommand>> Load(int entityId)
        {
            const string sql = @"
                SELECT EntityId, SequenceNo, CommandName, CommandType, Payload, Metadata, Timestamp
                FROM Commands
                WHERE EntityId = @EntityId
                ORDER BY SequenceNo ASC";

            var commands = new List<ICommand>();

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@EntityId", entityId);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var commandTypeString = reader.GetString(reader.GetOrdinal("CommandType"));
                var commandType = Type.GetType(commandTypeString);

                if (commandType == null)
                    continue;

                var payloadJson = reader.GetString(reader.GetOrdinal("Payload"));

                // Deserialize the entire command from JSON
                var command = JsonSerializer.Deserialize(payloadJson, commandType, _jsonOptions) as ICommand;

                if (command != null)
                {
                    commands.Add(command);
                }
            }

            return commands;
        }
    }
}
