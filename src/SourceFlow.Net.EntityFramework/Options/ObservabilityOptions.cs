using System;

namespace SourceFlow.Stores.EntityFramework.Options
{
    /// <summary>
    /// Configuration options for observability (OpenTelemetry tracing, metrics, logging)
    /// </summary>
    public class ObservabilityOptions
    {
        /// <summary>
        /// Gets or sets whether observability is enabled.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets the service name for telemetry.
        /// </summary>
        public string ServiceName { get; set; } = "SourceFlow.EntityFramework";

        /// <summary>
        /// Gets or sets the service version for telemetry.
        /// </summary>
        public string ServiceVersion { get; set; } = "1.0.0";

        /// <summary>
        /// Gets or sets tracing configuration.
        /// </summary>
        public TracingOptions Tracing { get; set; } = new TracingOptions();

        /// <summary>
        /// Gets or sets metrics configuration.
        /// </summary>
        public MetricsOptions Metrics { get; set; } = new MetricsOptions();
    }

    /// <summary>
    /// Configuration for distributed tracing.
    /// </summary>
    public class TracingOptions
    {
        /// <summary>
        /// Gets or sets whether tracing is enabled.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to trace database operations.
        /// </summary>
        public bool TraceDatabaseOperations { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to trace command operations.
        /// </summary>
        public bool TraceCommandOperations { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to include detailed SQL in traces.
        /// </summary>
        public bool IncludeSqlInTraces { get; set; } = false;

        /// <summary>
        /// Gets or sets the sampling ratio (0.0 to 1.0). 1.0 means trace everything.
        /// </summary>
        public double SamplingRatio { get; set; } = 1.0;
    }

    /// <summary>
    /// Configuration for metrics collection.
    /// </summary>
    public class MetricsOptions
    {
        /// <summary>
        /// Gets or sets whether metrics are enabled.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to collect database metrics.
        /// </summary>
        public bool CollectDatabaseMetrics { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to collect command metrics.
        /// </summary>
        public bool CollectCommandMetrics { get; set; } = true;

        /// <summary>
        /// Gets or sets the metrics collection interval in milliseconds.
        /// </summary>
        public int CollectionIntervalMs { get; set; } = 1000;
    }
}
