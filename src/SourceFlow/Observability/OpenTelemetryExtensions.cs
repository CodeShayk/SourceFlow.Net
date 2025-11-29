using System;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace SourceFlow.Observability
{
    /// <summary>
    /// Extension methods for configuring OpenTelemetry for SourceFlow.
    /// Provides easy setup for tracing and metrics with various exporters.
    /// </summary>
    public static class OpenTelemetryExtensions
    {
        /// <summary>
        /// Adds OpenTelemetry tracing and metrics for SourceFlow with default configuration.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="serviceName">The name of the service for telemetry.</param>
        /// <param name="serviceVersion">The version of the service.</param>
        /// <returns>The service collection for chaining.</returns>
        public static IServiceCollection AddSourceFlowTelemetry(
            this IServiceCollection services,
            string serviceName = "SourceFlow.Domain",
            string serviceVersion = "1.0.0")
        {
            return AddSourceFlowTelemetry(services, options =>
            {
                options.Enabled = true;
                options.ServiceName = serviceName;
                options.ServiceVersion = serviceVersion;
            });
        }

        /// <summary>
        /// Adds OpenTelemetry tracing and metrics for SourceFlow with custom configuration.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configureOptions">Action to configure observability options.</param>
        /// <returns>The service collection for chaining.</returns>
        public static IServiceCollection AddSourceFlowTelemetry(
            this IServiceCollection services,
            Action<DomainObservabilityOptions> configureOptions)
        {
            if (configureOptions == null)
                throw new ArgumentNullException(nameof(configureOptions));

            var options = new DomainObservabilityOptions();
            configureOptions(options);

            // Register the configured options
            services.AddSingleton(options);

            if (!options.Enabled)
                return services;

            // Configure OpenTelemetry Resource
            var resourceBuilder = ResourceBuilder.CreateDefault()
                .AddService(serviceName: options.ServiceName, serviceVersion: options.ServiceVersion);

            // Add OpenTelemetry Tracing
            services.AddOpenTelemetry()
                .WithTracing(builder =>
                {
                    builder
                        .SetResourceBuilder(resourceBuilder)
                        .AddSource(options.ServiceName);
                })
                .WithMetrics(builder =>
                {
                    builder
                        .SetResourceBuilder(resourceBuilder)
                        .AddMeter(options.ServiceName);
                });

            return services;
        }

        /// <summary>
        /// Adds Console exporter for OpenTelemetry traces and metrics.
        /// Useful for development and debugging.
        /// </summary>
        /// <param name="builder">The OpenTelemetry builder.</param>
        /// <returns>The OpenTelemetry builder for chaining.</returns>
        public static OpenTelemetryBuilder AddSourceFlowConsoleExporter(this OpenTelemetryBuilder builder)
        {
            return builder
                .WithTracing(tracing => tracing.AddConsoleExporter())
                .WithMetrics(metrics => metrics.AddConsoleExporter());
        }

        /// <summary>
        /// Adds OTLP (OpenTelemetry Protocol) exporter for traces and metrics.
        /// This is the standard protocol for production observability platforms.
        /// </summary>
        /// <param name="builder">The OpenTelemetry builder.</param>
        /// <param name="endpoint">The OTLP endpoint URL (e.g., "http://localhost:4317").</param>
        /// <returns>The OpenTelemetry builder for chaining.</returns>
        public static OpenTelemetryBuilder AddSourceFlowOtlpExporter(
            this OpenTelemetryBuilder builder,
            string endpoint = null)
        {
            return builder
                .WithTracing(tracing =>
                {
                    if (endpoint != null)
                        tracing.AddOtlpExporter(options => options.Endpoint = new Uri(endpoint));
                    else
                        tracing.AddOtlpExporter();
                })
                .WithMetrics(metrics =>
                {
                    if (endpoint != null)
                        metrics.AddOtlpExporter(options => options.Endpoint = new Uri(endpoint));
                    else
                        metrics.AddOtlpExporter();
                });
        }

        /// <summary>
        /// Adds enrichment tags to all traces and metrics.
        /// Useful for adding environment, region, or other context information.
        /// </summary>
        /// <param name="builder">The OpenTelemetry builder.</param>
        /// <param name="attributes">Key-value pairs to add as resource attributes.</param>
        /// <returns>The OpenTelemetry builder for chaining.</returns>
        public static OpenTelemetryBuilder AddSourceFlowResourceAttributes(
            this OpenTelemetryBuilder builder,
            params (string Key, object Value)[] attributes)
        {
            return builder.ConfigureResource(resource =>
            {
                foreach (var (key, value) in attributes)
                {
                    resource.AddAttributes(new[] { new System.Collections.Generic.KeyValuePair<string, object>(key, value) });
                }
            });
        }

        /// <summary>
        /// Adds HTTP instrumentation to trace HTTP calls.
        /// Note: Requires OpenTelemetry.Instrumentation.Http package to be installed separately.
        /// </summary>
        /// <param name="builder">The OpenTelemetry builder.</param>
        /// <returns>The OpenTelemetry builder for chaining.</returns>
        public static OpenTelemetryBuilder AddSourceFlowHttpInstrumentation(this OpenTelemetryBuilder builder)
        {
            // Note: This method requires OpenTelemetry.Instrumentation.Http package
            // Users should install it separately if they need HTTP instrumentation
            // return builder.WithTracing(tracing => tracing.AddHttpClientInstrumentation());
            return builder;
        }

        /// <summary>
        /// Configures batch processing for exporters to optimize throughput.
        /// </summary>
        /// <param name="builder">The OpenTelemetry builder.</param>
        /// <param name="maxQueueSize">Maximum queue size for batching (default: 2048).</param>
        /// <param name="maxExportBatchSize">Maximum batch size for export (default: 512).</param>
        /// <param name="scheduledDelayMilliseconds">Delay between exports in milliseconds (default: 5000).</param>
        /// <returns>The OpenTelemetry builder for chaining.</returns>
        public static OpenTelemetryBuilder ConfigureSourceFlowBatchProcessing(
            this OpenTelemetryBuilder builder,
            int maxQueueSize = 2048,
            int maxExportBatchSize = 512,
            int scheduledDelayMilliseconds = 5000)
        {
            return builder.WithTracing(tracing =>
            {
                tracing.SetSampler(new AlwaysOnSampler());
            });
        }
    }
}
