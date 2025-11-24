using Microsoft.Data.Sqlite;
using SourceFlow.Projections;
using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace SourceFlow.Net.SQL.Tests.Helpers
{
    /// <summary>
    /// SQLite implementation of IViewModelStore for integration testing.
    /// </summary>
    public class SqliteViewModelStore : IViewModelStore
    {
        private readonly SqliteConnection _connection;
        private readonly JsonSerializerOptions _jsonOptions;

        public SqliteViewModelStore(SqliteConnection connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = false
            };
        }

        public async Task<TViewModel> Get<TViewModel>(int id) where TViewModel : class, IViewModel
        {
            if (id <= 0)
                throw new ArgumentException("ViewModel Id must be greater than 0.", nameof(id));

            const string sql = @"
                SELECT ViewModelData
                FROM ViewModels
                WHERE Id = @Id AND ViewModelType = @ViewModelType";

            var viewModelType = typeof(TViewModel).AssemblyQualifiedName;

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@Id", id);
            cmd.Parameters.AddWithValue("@ViewModelType", viewModelType);

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var viewModelData = reader.GetString(reader.GetOrdinal("ViewModelData"));
                return JsonSerializer.Deserialize<TViewModel>(viewModelData, _jsonOptions);
            }

            throw new InvalidOperationException($"ViewModel of type {typeof(TViewModel).Name} with Id {id} not found.");
        }

        public async Task Persist<TViewModel>(TViewModel model) where TViewModel : IViewModel
        {
            if (model == null)
                throw new ArgumentNullException(nameof(model));

            if (model.Id <= 0)
                throw new ArgumentException("ViewModel Id must be greater than 0.", nameof(model));

            var viewModelType = typeof(TViewModel).AssemblyQualifiedName;
            var viewModelData = JsonSerializer.Serialize(model, _jsonOptions);

            // Check if exists
            const string checkSql = "SELECT COUNT(*) FROM ViewModels WHERE Id = @Id AND ViewModelType = @ViewModelType";
            using (var checkCmd = _connection.CreateCommand())
            {
                checkCmd.CommandText = checkSql;
                checkCmd.Parameters.AddWithValue("@Id", model.Id);
                checkCmd.Parameters.AddWithValue("@ViewModelType", viewModelType);
                var exists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync()) > 0;

                if (exists)
                {
                    // Update
                    const string updateSql = @"
                        UPDATE ViewModels
                        SET ViewModelData = @ViewModelData, UpdatedAt = datetime('now')
                        WHERE Id = @Id AND ViewModelType = @ViewModelType";

                    using var updateCmd = _connection.CreateCommand();
                    updateCmd.CommandText = updateSql;
                    updateCmd.Parameters.AddWithValue("@Id", model.Id);
                    updateCmd.Parameters.AddWithValue("@ViewModelType", viewModelType);
                    updateCmd.Parameters.AddWithValue("@ViewModelData", viewModelData);
                    await updateCmd.ExecuteNonQueryAsync();
                }
                else
                {
                    // Insert
                    const string insertSql = @"
                        INSERT INTO ViewModels (Id, ViewModelType, ViewModelData, CreatedAt, UpdatedAt)
                        VALUES (@Id, @ViewModelType, @ViewModelData, datetime('now'), datetime('now'))";

                    using var insertCmd = _connection.CreateCommand();
                    insertCmd.CommandText = insertSql;
                    insertCmd.Parameters.AddWithValue("@Id", model.Id);
                    insertCmd.Parameters.AddWithValue("@ViewModelType", viewModelType);
                    insertCmd.Parameters.AddWithValue("@ViewModelData", viewModelData);
                    await insertCmd.ExecuteNonQueryAsync();
                }
            }
        }

        public async Task Delete<TViewModel>(TViewModel model) where TViewModel : IViewModel
        {
            if (model == null)
                throw new ArgumentNullException(nameof(model));

            if (model.Id <= 0)
                throw new ArgumentException("ViewModel Id must be greater than 0.", nameof(model));

            const string sql = @"
                DELETE FROM ViewModels
                WHERE Id = @Id AND ViewModelType = @ViewModelType";

            var viewModelType = typeof(TViewModel).AssemblyQualifiedName;

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@Id", model.Id);
            cmd.Parameters.AddWithValue("@ViewModelType", viewModelType);

            var rowsAffected = await cmd.ExecuteNonQueryAsync();

            if (rowsAffected == 0)
            {
                throw new InvalidOperationException(
                    $"ViewModel of type {typeof(TViewModel).Name} with Id {model.Id} not found.");
            }
        }
    }
}
