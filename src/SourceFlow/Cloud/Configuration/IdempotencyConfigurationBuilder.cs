using System;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SourceFlow.Cloud.Configuration;

/// <summary>
/// Builder for configuring idempotency services in cloud integrations
/// </summary>
public class IdempotencyConfigurationBuilder
{
    private Action<IServiceCollection>? _configureAction;

    /// <summary>
    /// Use Entity Framework-based idempotency service for multi-instance deployments
    /// </summary>
    /// <param name="connectionString">Database connection string</param>
    /// <param name="cleanupIntervalMinutes">Cleanup interval in minutes (default: 60)</param>
    /// <returns>The builder for chaining</returns>
    /// <remarks>
    /// Requires the SourceFlow.Stores.EntityFramework package to be installed.
    /// This method uses reflection to call AddSourceFlowIdempotency to avoid direct dependency.
    /// </remarks>
    public IdempotencyConfigurationBuilder UseEFIdempotency(
        string connectionString, 
        int cleanupIntervalMinutes = 60)
    {
        if (string.IsNullOrEmpty(connectionString))
            throw new ArgumentException("Connection string cannot be null or empty.", nameof(connectionString));

        _configureAction = services =>
        {
            // Use reflection to call AddSourceFlowIdempotency from EntityFramework package
            var efExtensionsType = Type.GetType(
                "SourceFlow.Stores.EntityFramework.Extensions.ServiceCollectionExtensions, SourceFlow.Stores.EntityFramework");

            if (efExtensionsType == null)
            {
                throw new InvalidOperationException(
                    "SourceFlow.Stores.EntityFramework package is not installed. " +
                    "Install it using: dotnet add package SourceFlow.Stores.EntityFramework");
            }

            var method = efExtensionsType.GetMethod(
                "AddSourceFlowIdempotency",
                new[] { typeof(IServiceCollection), typeof(string), typeof(int) });

            if (method == null)
            {
                throw new InvalidOperationException(
                    "AddSourceFlowIdempotency method not found in SourceFlow.Stores.EntityFramework package. " +
                    "Ensure you have the latest version installed.");
            }

            method.Invoke(null, new object[] { services, connectionString, cleanupIntervalMinutes });
        };

        return this;
    }

    /// <summary>
    /// Use a custom idempotency service implementation
    /// </summary>
    /// <typeparam name="TImplementation">The custom idempotency service type</typeparam>
    /// <returns>The builder for chaining</returns>
    public IdempotencyConfigurationBuilder UseCustom<TImplementation>()
        where TImplementation : class, IIdempotencyService
    {
        _configureAction = services =>
        {
            services.AddScoped<IIdempotencyService, TImplementation>();
        };

        return this;
    }

    /// <summary>
    /// Use a custom idempotency service with factory
    /// </summary>
    /// <param name="factory">Factory function to create the idempotency service</param>
    /// <returns>The builder for chaining</returns>
    public IdempotencyConfigurationBuilder UseCustom(
        Func<IServiceProvider, IIdempotencyService> factory)
    {
        if (factory == null)
            throw new ArgumentNullException(nameof(factory));

        _configureAction = services =>
        {
            services.AddScoped(factory);
        };

        return this;
    }

    /// <summary>
    /// Explicitly use in-memory idempotency (this is the default if nothing is configured)
    /// </summary>
    /// <returns>The builder for chaining</returns>
    public IdempotencyConfigurationBuilder UseInMemory()
    {
        _configureAction = services =>
        {
            services.AddScoped<IIdempotencyService, InMemoryIdempotencyService>();
        };

        return this;
    }

    /// <summary>
    /// Builds and applies the idempotency configuration
    /// </summary>
    /// <param name="services">The service collection</param>
    public void Build(IServiceCollection services)
    {
        if (_configureAction != null)
        {
            _configureAction(services);
        }
        else
        {
            // Default to in-memory if nothing configured
            services.TryAddScoped<IIdempotencyService, InMemoryIdempotencyService>();
        }
    }

    /// <summary>
    /// Checks if any configuration has been set
    /// </summary>
    internal bool IsConfigured => _configureAction != null;
}
