#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Tasks;
using SourceFlow.Stores.EntityFramework.Options;

namespace SourceFlow.Stores.EntityFramework.Services
{
    /// <summary>
    /// Provides OpenTelemetry tracing and metrics for database operations.
    /// </summary>
    public class DatabaseTelemetryService : IDatabaseTelemetryService
    {
        private readonly ObservabilityOptions _options;
        private readonly ActivitySource? _activitySource;
        private readonly Meter? _meter;

        // Counters
        private readonly Counter<long>? _commandsAppended;
        private readonly Counter<long>? _commandsLoaded;
        private readonly Counter<long>? _entitiesPersisted;
        private readonly Counter<long>? _viewModelsPersisted;

        // Histograms
        private readonly Histogram<double>? _operationDuration;

        public DatabaseTelemetryService(SourceFlowEfOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            _options = options.Observability;

            if (_options.Enabled && _options.Tracing.Enabled)
            {
                _activitySource = new ActivitySource(
                    _options.ServiceName,
                    _options.ServiceVersion);
            }

            if (_options.Enabled && _options.Metrics.Enabled)
            {
                _meter = new Meter(
                    _options.ServiceName,
                    _options.ServiceVersion);

                _commandsAppended = _meter.CreateCounter<long>(
                    "sourceflow.commands.appended",
                    description: "Number of commands appended to the store");

                _commandsLoaded = _meter.CreateCounter<long>(
                    "sourceflow.commands.loaded",
                    description: "Number of commands loaded from the store");

                _entitiesPersisted = _meter.CreateCounter<long>(
                    "sourceflow.entities.persisted",
                    description: "Number of entities persisted to the store");

                _viewModelsPersisted = _meter.CreateCounter<long>(
                    "sourceflow.viewmodels.persisted",
                    description: "Number of view models persisted to the store");

                _operationDuration = _meter.CreateHistogram<double>(
                    "sourceflow.operation.duration",
                    unit: "ms",
                    description: "Duration of database operations in milliseconds");
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
            if (!_options.Enabled || !_options.Tracing.Enabled || _activitySource == null)
            {
                return await operation();
            }

            using var activity = _activitySource.StartActivity(operationName, ActivityKind.Internal);

            var stopwatch = Stopwatch.StartNew();
            try
            {
                // Enrich activity with custom attributes
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
            if (!_options.Enabled || !_options.Tracing.Enabled || _activitySource == null)
            {
                await operation();
                return;
            }

            using var activity = _activitySource.StartActivity(operationName, ActivityKind.Internal);

            var stopwatch = Stopwatch.StartNew();
            try
            {
                // Enrich activity with custom attributes
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
        /// Records a command append metric.
        /// </summary>
        public void RecordCommandAppended()
        {
            if (_options.Enabled && _options.Metrics.Enabled && _options.Metrics.CollectCommandMetrics)
            {
                _commandsAppended?.Add(1);
            }
        }

        /// <summary>
        /// Records a command load metric.
        /// </summary>
        public void RecordCommandsLoaded(int count)
        {
            if (_options.Enabled && _options.Metrics.Enabled && _options.Metrics.CollectCommandMetrics)
            {
                _commandsLoaded?.Add(count);
            }
        }

        /// <summary>
        /// Records an entity persist metric.
        /// </summary>
        public void RecordEntityPersisted()
        {
            if (_options.Enabled && _options.Metrics.Enabled && _options.Metrics.CollectDatabaseMetrics)
            {
                _entitiesPersisted?.Add(1);
            }
        }

        /// <summary>
        /// Records a view model persist metric.
        /// </summary>
        public void RecordViewModelPersisted()
        {
            if (_options.Enabled && _options.Metrics.Enabled && _options.Metrics.CollectDatabaseMetrics)
            {
                _viewModelsPersisted?.Add(1);
            }
        }
    }

    /// <summary>
    /// Interface for database telemetry service.
    /// </summary>
    public interface IDatabaseTelemetryService
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
        /// Records a command append metric.
        /// </summary>
        void RecordCommandAppended();

        /// <summary>
        /// Records a command load metric.
        /// </summary>
        void RecordCommandsLoaded(int count);

        /// <summary>
        /// Records an entity persist metric.
        /// </summary>
        void RecordEntityPersisted();

        /// <summary>
        /// Records a view model persist metric.
        /// </summary>
        void RecordViewModelPersisted();
    }
}
