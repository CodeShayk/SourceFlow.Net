#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Tasks;

namespace SourceFlow.Observability
{
    /// <summary>
    /// Provides OpenTelemetry tracing and metrics for domain-level operations.
    /// Tracks aggregate commands, saga execution, and serialization operations.
    /// </summary>
    public class DomainTelemetryService : IDomainTelemetryService
    {
        private readonly DomainObservabilityOptions _options;
        private readonly ActivitySource? _activitySource;
        private readonly Meter? _meter;

        // Counters
        private readonly Counter<long>? _commandsExecuted;

        private readonly Counter<long>? _sagasExecuted;
        private readonly Counter<long>? _entitiesCreated;
        private readonly Counter<long>? _serializationOperations;

        // Histograms
        private readonly Histogram<double>? _operationDuration;

        private readonly Histogram<double>? _serializationDuration;

        public DomainTelemetryService(DomainObservabilityOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));

            if (_options.Enabled)
            {
                _activitySource = new ActivitySource(
                    _options.ServiceName,
                    _options.ServiceVersion);

                _meter = new Meter(
                    _options.ServiceName,
                    _options.ServiceVersion);

                _commandsExecuted = _meter.CreateCounter<long>(
                    "sourceflow.domain.commands.executed",
                    description: "Number of aggregate commands executed");

                _sagasExecuted = _meter.CreateCounter<long>(
                    "sourceflow.domain.sagas.executed",
                    description: "Number of sagas executed");

                _entitiesCreated = _meter.CreateCounter<long>(
                    "sourceflow.domain.entities.created",
                    description: "Number of entities created");

                _serializationOperations = _meter.CreateCounter<long>(
                    "sourceflow.domain.serialization.operations",
                    description: "Number of serialization/deserialization operations");

                _operationDuration = _meter.CreateHistogram<double>(
                    "sourceflow.domain.operation.duration",
                    unit: "ms",
                    description: "Duration of domain operations in milliseconds");

                _serializationDuration = _meter.CreateHistogram<double>(
                    "sourceflow.domain.serialization.duration",
                    unit: "ms",
                    description: "Duration of serialization operations in milliseconds");
            }
        }

        /// <summary>
        /// Executes an async operation with telemetry tracking.
        /// </summary>
        public async Task<T> TraceAsync<T>(
            string operationName,
            Func<Task<T>> operation,
            Action<Activity>? enrichActivity = null)
        {
            if (!_options.Enabled || _activitySource == null)
            {
                return await operation();
            }

            using var activity = _activitySource.StartActivity(operationName, ActivityKind.Internal);

            var stopwatch = Stopwatch.StartNew();
            try
            {
                enrichActivity?.Invoke(activity!);

                var result = await operation();

                activity?.SetStatus(ActivityStatusCode.Ok);

                return result;
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.SetTag("exception.type", ex.GetType().FullName);
                activity?.SetTag("exception.message", ex.Message);
                throw;
            }
            finally
            {
                stopwatch.Stop();
                _operationDuration?.Record(stopwatch.Elapsed.TotalMilliseconds,
                    new KeyValuePair<string, object?>("operation", operationName));
            }
        }

        /// <summary>
        /// Executes an async operation with telemetry tracking.
        /// </summary>
        public async Task TraceAsync(
            string operationName,
            Func<Task> operation,
            Action<Activity>? enrichActivity = null)
        {
            if (!_options.Enabled || _activitySource == null)
            {
                await operation();
                return;
            }

            using var activity = _activitySource.StartActivity(operationName, ActivityKind.Internal);

            var stopwatch = Stopwatch.StartNew();
            try
            {
                enrichActivity?.Invoke(activity!);

                await operation();

                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.SetTag("exception.type", ex.GetType().FullName);
                activity?.SetTag("exception.message", ex.Message);
                throw;
            }
            finally
            {
                stopwatch.Stop();
                _operationDuration?.Record(stopwatch.Elapsed.TotalMilliseconds,
                    new KeyValuePair<string, object?>("operation", operationName));
            }
        }

        /// <summary>
        /// Traces a serialization operation with duration tracking.
        /// </summary>
        public T TraceSerialization<T>(
            string operationType,
            Func<T> operation,
            Action<Activity>? enrichActivity = null)
        {
            if (!_options.Enabled || _activitySource == null)
            {
                return operation();
            }

            using var activity = _activitySource.StartActivity($"sourceflow.domain.{operationType}", ActivityKind.Internal);

            var stopwatch = Stopwatch.StartNew();
            try
            {
                enrichActivity?.Invoke(activity!);

                var result = operation();

                activity?.SetStatus(ActivityStatusCode.Ok);
                _serializationOperations?.Add(1, new KeyValuePair<string, object?>("operation", operationType));

                return result;
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.SetTag("exception.type", ex.GetType().FullName);
                activity?.SetTag("exception.message", ex.Message);
                throw;
            }
            finally
            {
                stopwatch.Stop();
                _serializationDuration?.Record(stopwatch.Elapsed.TotalMilliseconds,
                    new KeyValuePair<string, object?>("operation", operationType));
            }
        }

        /// <summary>
        /// Records a command execution metric.
        /// </summary>
        public void RecordCommandExecuted(string commandType, int entityId)
        {
            if (_options.Enabled)
            {
                _commandsExecuted?.Add(1,
                    new KeyValuePair<string, object?>("command_type", commandType),
                    new KeyValuePair<string, object?>("entity_id", entityId));
            }
        }

        /// <summary>
        /// Records a saga execution metric.
        /// </summary>
        public void RecordSagaExecuted(string sagaType)
        {
            if (_options.Enabled)
            {
                _sagasExecuted?.Add(1,
                    new KeyValuePair<string, object?>("saga_type", sagaType));
            }
        }

        /// <summary>
        /// Records an entity creation metric.
        /// </summary>
        public void RecordEntityCreated(string entityType)
        {
            if (_options.Enabled)
            {
                _entitiesCreated?.Add(1,
                    new KeyValuePair<string, object?>("entity_type", entityType));
            }
        }
    }

    /// <summary>
    /// Interface for domain telemetry service.
    /// </summary>
    public interface IDomainTelemetryService
    {
        /// <summary>
        /// Executes an async operation with telemetry tracking.
        /// </summary>
        Task<T> TraceAsync<T>(string operationName, Func<Task<T>> operation, Action<Activity>? enrichActivity = null);

        /// <summary>
        /// Executes an async operation with telemetry tracking.
        /// </summary>
        Task TraceAsync(string operationName, Func<Task> operation, Action<Activity>? enrichActivity = null);

        /// <summary>
        /// Traces a serialization operation with duration tracking.
        /// </summary>
        T TraceSerialization<T>(string operationType, Func<T> operation, Action<Activity>? enrichActivity = null);

        /// <summary>
        /// Records a command execution metric.
        /// </summary>
        void RecordCommandExecuted(string commandType, int entityId);

        /// <summary>
        /// Records a saga execution metric.
        /// </summary>
        void RecordSagaExecuted(string sagaType);

        /// <summary>
        /// Records an entity creation metric.
        /// </summary>
        void RecordEntityCreated(string entityType);
    }

    /// <summary>
    /// Configuration options for domain-level observability.
    /// </summary>
    public class DomainObservabilityOptions
    {
        /// <summary>
        /// Gets or sets whether observability is enabled.
        /// </summary>
        public bool Enabled { get; set; } = false;  // Disabled by default to avoid breaking changes

        /// <summary>
        /// Gets or sets the service name for telemetry.
        /// </summary>
        public string ServiceName { get; set; } = "SourceFlow.Domain";

        /// <summary>
        /// Gets or sets the service version for telemetry.
        /// </summary>
        public string ServiceVersion { get; set; } = "1.0.0";
    }
}
