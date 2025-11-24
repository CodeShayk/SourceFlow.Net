using Microsoft.Data.Sqlite;
using System.Threading.Tasks;

namespace SourceFlow.Net.SQL.Tests.Helpers
{
    /// <summary>
    /// Helper class for SQLite integration tests.
    /// </summary>
    public class SqliteTestHelper
    {
        private readonly string _connectionString;

        public SqliteTestHelper(string connectionString)
        {
            _connectionString = connectionString;
        }

        /// <summary>
        /// Creates an in-memory SQLite database with the required schema.
        /// </summary>
        public static async Task<SqliteConnection> CreateInMemoryDatabase()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            return connection;
        }

        /// <summary>
        /// Creates the Commands table schema.
        /// </summary>
        public static async Task CreateCommandsTable(SqliteConnection connection, string schema = "main")
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS Commands (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    EntityId INTEGER NOT NULL,
                    SequenceNo INTEGER NOT NULL,
                    CommandName TEXT,
                    CommandType TEXT NOT NULL,
                    Payload TEXT NOT NULL,
                    Metadata TEXT NOT NULL,
                    Timestamp TEXT NOT NULL,
                    CreatedAt TEXT DEFAULT (datetime('now')),
                    UNIQUE(EntityId, SequenceNo)
                );
                CREATE INDEX IF NOT EXISTS IX_Commands_EntityId ON Commands(EntityId);
                CREATE INDEX IF NOT EXISTS IX_Commands_EntityId_SequenceNo ON Commands(EntityId, SequenceNo);";

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Creates the Entities table schema.
        /// </summary>
        public static async Task CreateEntitiesTable(SqliteConnection connection, string schema = "main")
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS Entities (
                    Id INTEGER NOT NULL,
                    EntityType TEXT NOT NULL,
                    EntityData TEXT NOT NULL,
                    CreatedAt TEXT DEFAULT (datetime('now')),
                    UpdatedAt TEXT DEFAULT (datetime('now')),
                    PRIMARY KEY (Id, EntityType)
                );
                CREATE INDEX IF NOT EXISTS IX_Entities_EntityType ON Entities(EntityType);";

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Creates the ViewModels table schema.
        /// </summary>
        public static async Task CreateViewModelsTable(SqliteConnection connection, string schema = "main")
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS ViewModels (
                    Id INTEGER NOT NULL,
                    ViewModelType TEXT NOT NULL,
                    ViewModelData TEXT NOT NULL,
                    CreatedAt TEXT DEFAULT (datetime('now')),
                    UpdatedAt TEXT DEFAULT (datetime('now')),
                    PRIMARY KEY (Id, ViewModelType)
                );
                CREATE INDEX IF NOT EXISTS IX_ViewModels_ViewModelType ON ViewModels(ViewModelType);";

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Creates all required tables for integration tests.
        /// </summary>
        public static async Task CreateAllTables(SqliteConnection connection)
        {
            await CreateCommandsTable(connection);
            await CreateEntitiesTable(connection);
            await CreateViewModelsTable(connection);
        }

        /// <summary>
        /// Clears all data from a table.
        /// </summary>
        public static async Task ClearTable(SqliteConnection connection, string tableName)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM {tableName}";
            await command.ExecuteNonQueryAsync();
        }
    }
}
