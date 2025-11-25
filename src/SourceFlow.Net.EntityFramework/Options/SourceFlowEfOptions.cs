using System;
using System.ComponentModel.DataAnnotations;

namespace SourceFlow.Stores.EntityFramework.Options
{
    /// <summary>
    /// Configuration options for Entity Framework stores
    /// </summary>
    public class SourceFlowEfOptions
    {
        /// <summary>
        /// Connection string for command store
        /// </summary>
        public string? CommandConnectionString { get; set; }
        
        /// <summary>
        /// Connection string for entity store
        /// </summary>
        public string? EntityConnectionString { get; set; }
        
        /// <summary>
        /// Connection string for view model store
        /// </summary>
        public string? ViewModelConnectionString { get; set; }
        
        /// <summary>
        /// If true, a single connection string will be used for all stores
        /// </summary>
        public string? DefaultConnectionString { get; set; }

        /// <summary>
        /// Table naming convention for entity tables.
        /// </summary>
        public TableNamingConvention EntityTableNaming { get; set; } = new TableNamingConvention();

        /// <summary>
        /// Table naming convention for view model tables.
        /// </summary>
        public TableNamingConvention ViewModelTableNaming { get; set; } = new TableNamingConvention();

        /// <summary>
        /// Table naming convention for command tables.
        /// </summary>
        public TableNamingConvention CommandTableNaming { get; set; } = new TableNamingConvention();

        /// <summary>
        /// Gets the connection string for a specific store type
        /// </summary>
        /// <param name="storeType">Type of store</param>
        /// <returns>Appropriate connection string</returns>
        public string GetConnectionString(StoreType storeType)
        {
            return storeType switch
            {
                StoreType.Command => CommandConnectionString ?? DefaultConnectionString 
                    ?? throw new InvalidOperationException("Command connection string not configured"),
                StoreType.Entity => EntityConnectionString ?? DefaultConnectionString 
                    ?? throw new InvalidOperationException("Entity connection string not configured"),
                StoreType.ViewModel => ViewModelConnectionString ?? DefaultConnectionString 
                    ?? throw new InvalidOperationException("ViewModel connection string not configured"),
                _ => throw new ArgumentException($"Unknown store type: {storeType}", nameof(storeType))
            };
        }
    }
    
    /// <summary>
    /// Enum representing different store types
    /// </summary>
    public enum StoreType
    {
        Command,
        Entity,
        ViewModel
    }
}