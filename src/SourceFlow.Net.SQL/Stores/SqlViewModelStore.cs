using System;
using System.Data.SqlClient;
using System.Text.Json;
using System.Threading.Tasks;
using SourceFlow.Net.SQL.Configuration;
using SourceFlow.Projections;

namespace SourceFlow.Net.SQL.Stores
{
    /// <summary>
    /// SQL Server implementation of IViewModelStore using ADO.NET.
    /// </summary>
    public class SqlViewModelStore : IViewModelStore
    {
        private readonly string _connectionString;
        private readonly string _schema;
        private readonly int _commandTimeout;
        private readonly JsonSerializerOptions _jsonOptions;

        /// <summary>
        /// Initializes a new instance of SqlViewModelStore.
        /// </summary>
        /// <param name="configuration">SQL store configuration</param>
        public SqlViewModelStore(SqlStoreConfiguration configuration)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));

            configuration.Validate();

            _connectionString = configuration.ViewModelStoreConnectionString;
            _schema = configuration.ViewModelStoreSchema;
            _commandTimeout = configuration.CommandTimeout;

            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = false
            };
        }

        /// <summary>
        /// Retrieves a view model by unique identifier.
        /// </summary>
        public async Task<TViewModel> Get<TViewModel>(int id) where TViewModel : class, IViewModel
        {
            if (id <= 0)
                throw new ArgumentException("ViewModel Id must be greater than 0.", nameof(id));

            const string sql = @"
                SELECT ViewModelData
                FROM [{0}].[ViewModels]
                WHERE Id = @Id AND ViewModelType = @ViewModelType";

            var formattedSql = string.Format(sql, _schema);
            var viewModelType = typeof(TViewModel).AssemblyQualifiedName;

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                using (var cmd = new SqlCommand(formattedSql, connection))
                {
                    cmd.CommandTimeout = _commandTimeout;
                    cmd.Parameters.AddWithValue("@Id", id);
                    cmd.Parameters.AddWithValue("@ViewModelType", viewModelType);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            var viewModelData = reader.GetString(reader.GetOrdinal("ViewModelData"));
                            return JsonSerializer.Deserialize<TViewModel>(viewModelData, _jsonOptions);
                        }
                    }
                }
            }

            throw new InvalidOperationException($"ViewModel of type {typeof(TViewModel).Name} with Id {id} not found.");
        }

        /// <summary>
        /// Creates or updates a view model to the store.
        /// </summary>
        public async Task Persist<TViewModel>(TViewModel model) where TViewModel : IViewModel
        {
            if (model == null)
                throw new ArgumentNullException(nameof(model));

            if (model.Id <= 0)
                throw new ArgumentException("ViewModel Id must be greater than 0.", nameof(model));

            const string sql = @"
                MERGE [{0}].[ViewModels] AS target
                USING (SELECT @Id AS Id, @ViewModelType AS ViewModelType) AS source
                ON (target.Id = source.Id AND target.ViewModelType = source.ViewModelType)
                WHEN MATCHED THEN
                    UPDATE SET
                        ViewModelData = @ViewModelData,
                        UpdatedAt = GETUTCDATE()
                WHEN NOT MATCHED THEN
                    INSERT (Id, ViewModelType, ViewModelData, CreatedAt, UpdatedAt)
                    VALUES (@Id, @ViewModelType, @ViewModelData, GETUTCDATE(), GETUTCDATE());";

            var formattedSql = string.Format(sql, _schema);
            var viewModelType = typeof(TViewModel).AssemblyQualifiedName;
            var viewModelData = JsonSerializer.Serialize(model, _jsonOptions);

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                using (var cmd = new SqlCommand(formattedSql, connection))
                {
                    cmd.CommandTimeout = _commandTimeout;
                    cmd.Parameters.AddWithValue("@Id", model.Id);
                    cmd.Parameters.AddWithValue("@ViewModelType", viewModelType);
                    cmd.Parameters.AddWithValue("@ViewModelData", viewModelData);

                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }

        /// <summary>
        /// Deletes a view model from the store.
        /// </summary>
        public async Task Delete<TViewModel>(TViewModel model) where TViewModel : IViewModel
        {
            if (model == null)
                throw new ArgumentNullException(nameof(model));

            if (model.Id <= 0)
                throw new ArgumentException("ViewModel Id must be greater than 0.", nameof(model));

            const string sql = @"
                DELETE FROM [{0}].[ViewModels]
                WHERE Id = @Id AND ViewModelType = @ViewModelType";

            var formattedSql = string.Format(sql, _schema);
            var viewModelType = typeof(TViewModel).AssemblyQualifiedName;

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                using (var cmd = new SqlCommand(formattedSql, connection))
                {
                    cmd.CommandTimeout = _commandTimeout;
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

                IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[{0}].[ViewModels]') AND type in (N'U'))
                BEGIN
                    CREATE TABLE [{0}].[ViewModels] (
                        Id INT NOT NULL,
                        ViewModelType NVARCHAR(500) NOT NULL,
                        ViewModelData NVARCHAR(MAX) NOT NULL,
                        CreatedAt DATETIME2 DEFAULT GETUTCDATE(),
                        UpdatedAt DATETIME2 DEFAULT GETUTCDATE(),
                        PRIMARY KEY (Id, ViewModelType),
                        INDEX IX_ViewModels_ViewModelType (ViewModelType)
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
