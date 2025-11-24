using System;

namespace SourceFlow.Net.SQL.Configuration
{
    /// <summary>
    /// Configuration for SQL-based persistence stores.
    /// </summary>
    public class SqlStoreConfiguration
    {
        /// <summary>
        /// Connection string for the Command Store database.
        /// </summary>
        public string CommandStoreConnectionString { get; set; }

        /// <summary>
        /// Connection string for the Entity Store database.
        /// </summary>
        public string EntityStoreConnectionString { get; set; }

        /// <summary>
        /// Connection string for the ViewModel Store database.
        /// </summary>
        public string ViewModelStoreConnectionString { get; set; }

        /// <summary>
        /// Schema name for Command Store tables. Default is 'dbo'.
        /// </summary>
        public string CommandStoreSchema { get; set; } = "dbo";

        /// <summary>
        /// Schema name for Entity Store tables. Default is 'dbo'.
        /// </summary>
        public string EntityStoreSchema { get; set; } = "dbo";

        /// <summary>
        /// Schema name for ViewModel Store tables. Default is 'dbo'.
        /// </summary>
        public string ViewModelStoreSchema { get; set; } = "dbo";

        /// <summary>
        /// Command timeout in seconds. Default is 30 seconds.
        /// </summary>
        public int CommandTimeout { get; set; } = 30;

        /// <summary>
        /// Validates the configuration.
        /// </summary>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(CommandStoreConnectionString))
                throw new ArgumentException("CommandStoreConnectionString is required.", nameof(CommandStoreConnectionString));

            if (string.IsNullOrWhiteSpace(EntityStoreConnectionString))
                throw new ArgumentException("EntityStoreConnectionString is required.", nameof(EntityStoreConnectionString));

            if (string.IsNullOrWhiteSpace(ViewModelStoreConnectionString))
                throw new ArgumentException("ViewModelStoreConnectionString is required.", nameof(ViewModelStoreConnectionString));

            if (CommandTimeout <= 0)
                throw new ArgumentException("CommandTimeout must be greater than 0.", nameof(CommandTimeout));
        }
    }
}
