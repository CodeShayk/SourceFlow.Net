# SourceFlow.Net.EntityFramework Enhancements

This document describes the advanced features for production-grade applications: Resilience, Observability, and Memory Optimization.

## Table of Contents

- [Resilience with Polly](#resilience-with-polly)
- [Observability with OpenTelemetry](#observability-with-opentelemetry)
- [Memory Optimization with ArrayPool](#memory-optimization-with-arraypool)
- [Configuration Examples](#configuration-examples)

## Resilience with Polly

Polly provides fault-tolerance and resilience patterns for handling transient failures in database operations.

###Features

- **Retry Policy**: Automatically retry failed operations with exponential backoff
- **Circuit Breaker**: Prevent cascading failures by breaking the circuit after repeated failures
- **Timeout**: Enforce maximum execution time for operations

### Configuration

```csharp
services.AddSourceFlowEfStores(options =>
{
    options.DefaultConnectionString = connectionString;

    // Configure resilience
    options.Resilience.Enabled = true;

    // Retry configuration
    options.Resilience.Retry.Enabled = true;
    options.Resilience.Retry.MaxRetryAttempts = 3;
    options.Resilience.Retry.BaseDelayMs = 1000;          // 1 second base delay
    options.Resilience.Retry.MaxDelayMs = 30000;          // 30 seconds max delay
    options.Resilience.Retry.UseExponentialBackoff = true;
    options.Resilience.Retry.UseJitter = true;            // Prevents thundering herd

    // Circuit breaker configuration
    options.Resilience.CircuitBreaker.Enabled = true;
    options.Resilience.CircuitBreaker.FailureThreshold = 5;      // Break after 5 failures
    options.Resilience.CircuitBreaker.BreakDurationMs = 30000;   // Stay open for 30 seconds
    options.Resilience.CircuitBreaker.SuccessThreshold = 2;      // 2 successes to close

    // Timeout configuration
    options.Resilience.Timeout.Enabled = true;
    options.Resilience.Timeout.TimeoutMs = 30000;         // 30 second timeout
});
```

### How It Works

When resilience is enabled, all database operations are automatically wrapped with resilience policies:

1. **Timeout Policy**: Ensures operations complete within the specified time
2. **Retry Policy**: Retries failed operations with exponential backoff and jitter
3. **Circuit Breaker**: Breaks the circuit after repeated failures to prevent resource exhaustion

Example flow for a database save operation:
```
Operation Attempt
    ↓
Timeout Policy Applied
    ↓
Retry Policy Applied (with exponential backoff)
    ↓
Circuit Breaker Check
    ↓
Execute Database Operation
    ↓
Success or Failure Recorded
```

### Benefits

- **Transient Failure Handling**: Automatically recovers from temporary database connection issues
- **Prevents Cascading Failures**: Circuit breaker stops calling failing services
- **Resource Protection**: Timeouts prevent hanging operations
- **Self-Healing**: System automatically recovers when service becomes available

## Observability with OpenTelemetry

OpenTelemetry provides distributed tracing, metrics, and logging for comprehensive system observability.

### Features

- **Distributed Tracing**: Track requests across service boundaries
- **Metrics Collection**: Monitor performance and health metrics
- **Entity Framework Instrumentation**: Automatic SQL query tracing
- **Custom Spans**: Add business-level tracing

### Configuration

```csharp
services.AddSourceFlowEfStores(options =>
{
    options.DefaultConnectionString = connectionString;

    // Configure observability
    options.Observability.Enabled = true;
    options.Observability.ServiceName = "MyApplication";
    options.Observability.ServiceVersion = "1.0.0";

    // Tracing configuration
    options.Observability.Tracing.Enabled = true;
    options.Observability.Tracing.TraceDatabaseOperations = true;
    options.Observability.Tracing.TraceCommandOperations = true;
    options.Observability.Tracing.IncludeSqlInTraces = false;  // Set to true for debugging
    options.Observability.Tracing.SamplingRatio = 1.0;         // Trace 100% (adjust in production)

    // Metrics configuration
    options.Observability.Metrics.Enabled = true;
    options.Observability.Metrics.CollectDatabaseMetrics = true;
    options.Observability.Metrics.CollectCommandMetrics = true;
    options.Observability.Metrics.CollectionIntervalMs = 1000;
});

// Configure OpenTelemetry exporters
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource("SourceFlow.EntityFramework")
        .AddEntityFrameworkCoreInstrumentation()
        .AddConsoleExporter()
        .AddJaegerExporter()        // Or your preferred exporter
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddMeter("SourceFlow.EntityFramework")
        .AddConsoleExporter()
        .AddPrometheusExporter());
```

### Traces Collected

**Database Operations:**
- `sourceflow.ef.command.append` - Command storage operations
- `sourceflow.ef.command.load` - Command loading operations
- `sourceflow.ef.entity.persist` - Entity persistence operations
- `sourceflow.ef.viewmodel.persist` - View model persistence operations

**Attributes Included:**
- `db.system` - Database system (e.g., "sqlserver", "sqlite")
- `db.name` - Database name
- `db.operation` - Operation type (e.g., "INSERT", "SELECT")
- `sourceflow.entity_id` - Entity ID
- `sourceflow.sequence_no` - Command sequence number
- `sourceflow.command_type` - Command type name

### Metrics Collected

- `sourceflow.commands.appended` - Counter of appended commands
- `sourceflow.commands.loaded` - Counter of loaded commands
- `sourceflow.entities.persisted` - Counter of persisted entities
- `sourceflow.viewmodels.persisted` - Counter of persisted view models
- `sourceflow.operation.duration` - Histogram of operation durations
- `sourceflow.database.connections` - Gauge of active database connections

### Viewing Traces

**Jaeger (Recommended for Development):**
```bash
docker run -d --name jaeger \
  -p 16686:16686 \
  -p 4318:4318 \
  jaegertracing/all-in-one:latest

# View UI at http://localhost:16686
```

**Console Exporter (Simple Debugging):**
Traces are written to console output in development.

## Memory Optimization with ArrayPool

ArrayPool reduces GC pressure by reusing byte arrays for serialization operations.

### When to Use

ArrayPool is beneficial for:
- High-throughput scenarios (>1000 commands/second)
- Large payload sizes (>10KB)
- Memory-constrained environments
- Reducing GC pause times

### Implementation Pattern

```csharp
// Example: Optimized serialization with ArrayPool
using System.Buffers;
using System.Text.Json;

public class OptimizedCommandStoreAdapter
{
    private static readonly ArrayPool<byte> _byteArrayPool = ArrayPool<byte>.Shared;

    public async Task Append(ICommand command)
    {
        byte[]? rentedBuffer = null;
        try
        {
            // Estimate buffer size (can be tuned based on your payload sizes)
            int estimatedSize = EstimatePayloadSize(command.Payload);
            rentedBuffer = _byteArrayPool.Rent(estimatedSize);

            // Use the rented buffer for serialization
            var bytesWritten = SerializeToBuffer(command, rentedBuffer);

            // Process only the used portion
            var usedSpan = rentedBuffer.AsSpan(0, bytesWritten);

            await ProcessSerializedCommand(usedSpan);
        }
        finally
        {
            // Always return the buffer to the pool
            if (rentedBuffer != null)
            {
                _byteArrayPool.Return(rentedBuffer, clearArray: true);
            }
        }
    }

    private int EstimatePayloadSize(object payload)
    {
        // Conservative estimate: most payloads are < 4KB
        // Adjust based on your domain
        return 4096;  // 4KB default
    }
}
```

### Best Practices

1. **Always Return Buffers**: Use try-finally to ensure buffers are returned
2. **Clear Sensitive Data**: Use `clearArray: true` when returning buffers with sensitive information
3. **Size Appropriately**: Rent slightly larger buffers to avoid re-allocation
4. **Don't Hold Long**: Return buffers as soon as possible
5. **Measure First**: Profile to confirm GC pressure before optimizing

### Performance Impact

Expected improvements with ArrayPool (high-throughput scenarios):

- **GC Pressure**: 60-80% reduction in Gen0/Gen1 collections
- **Memory Allocation**: 50-70% reduction in byte array allocations
- **Throughput**: 10-20% improvement in commands/second
- **Latency**: P99 latency improvement of 15-30%

**Note**: Impact varies based on payload size and throughput. Always measure in your specific scenario.

## Configuration Examples

### Production Configuration

```csharp
services.AddSourceFlowEfStores(options =>
{
    options.DefaultConnectionString = configuration.GetConnectionString("SourceFlow");

    // Resilience: Production settings
    options.Resilience.Enabled = true;
    options.Resilience.Retry.MaxRetryAttempts = 3;
    options.Resilience.Retry.UseExponentialBackoff = true;
    options.Resilience.Retry.UseJitter = true;
    options.Resilience.CircuitBreaker.Enabled = true;
    options.Resilience.CircuitBreaker.FailureThreshold = 10;
    options.Resilience.CircuitBreaker.BreakDurationMs = 60000;  // 1 minute

    // Observability: Production settings
    options.Observability.Enabled = true;
    options.Observability.ServiceName = "ProductionApp";
    options.Observability.Tracing.Enabled = true;
    options.Observability.Tracing.IncludeSqlInTraces = false;  // Don't log SQL in production
    options.Observability.Tracing.SamplingRatio = 0.1;         // Sample 10% of requests
    options.Observability.Metrics.Enabled = true;
});
```

### Development Configuration

```csharp
services.AddSourceFlowEfStores(options =>
{
    options.DefaultConnectionString = "Data Source=dev.db";

    // Resilience: Disabled for easier debugging
    options.Resilience.Enabled = false;

    // Observability: Full tracing for debugging
    options.Observability.Enabled = true;
    options.Observability.ServiceName = "DevApp";
    options.Observability.Tracing.Enabled = true;
    options.Observability.Tracing.IncludeSqlInTraces = true;   // Show SQL in dev
    options.Observability.Tracing.SamplingRatio = 1.0;          // Trace everything
    options.Observability.Metrics.Enabled = true;
});
```

### High-Throughput Configuration

```csharp
services.AddSourceFlowEfStores(options =>
{
    options.DefaultConnectionString = connectionString;

    // Resilience: Optimized for throughput
    options.Resilience.Enabled = true;
    options.Resilience.Retry.MaxRetryAttempts = 2;        // Fewer retries
    options.Resilience.Retry.BaseDelayMs = 500;           // Faster retries
    options.Resilience.Timeout.TimeoutMs = 10000;         // Shorter timeout

    // Observability: Reduced overhead
    options.Observability.Enabled = true;
    options.Observability.Tracing.Enabled = true;
    options.Observability.Tracing.SamplingRatio = 0.01;   // Sample 1%
    options.Observability.Metrics.Enabled = true;

    // Use ArrayPool for memory optimization (implement in custom adapter)
});
```

## Monitoring and Alerts

### Key Metrics to Monitor

**Resilience:**
- `polly.circuit_breaker.state` - Circuit breaker state (Closed/Open/HalfOpen)
- `polly.retry.count` - Number of retries per operation
- `polly.timeout.count` - Number of timeouts

**Performance:**
- `sourceflow.operation.duration` - P50, P95, P99 latencies
- `sourceflow.commands.appended.rate` - Commands per second
- `sourceflow.database.connections` - Connection pool utilization

**Errors:**
- `sourceflow.errors.total` - Total error count
- `sourceflow.errors.by_type` - Errors grouped by type

### Recommended Alerts

1. **Circuit Breaker Open**: Alert when circuit breaker opens
2. **High Retry Rate**: Alert when retry rate > 10%
3. **P99 Latency**: Alert when P99 > SLA threshold
4. **Error Rate**: Alert when error rate > 1%
5. **Connection Pool**: Alert when utilization > 80%

## Troubleshooting

### Resilience Issues

**Circuit breaker constantly opening:**
- Check `FailureThreshold` - may be too low
- Check database health and connection string
- Review `BreakDurationMs` - may be too short

**Too many retries:**
- Reduce `MaxRetryAttempts`
- Increase `BaseDelayMs` to slow down retries
- Check if failures are transient or persistent

### Observability Issues

**No traces appearing:**
- Verify `Observability.Enabled = true`
- Check exporter configuration
- Verify `SamplingRatio` > 0
- Check firewall rules for exporter endpoints

**High overhead from tracing:**
- Reduce `SamplingRatio`
- Disable `IncludeSqlInTraces`
- Use head-based sampling instead of tail-based

### Memory Optimization Issues

**No performance improvement:**
- Profile to confirm GC was the bottleneck
- Check payload sizes (ArrayPool helps most with >1KB payloads)
- Verify buffers are being returned to pool

## Next Steps

1. **Start with Observability**: Gain visibility into system behavior
2. **Add Resilience**: Protect against transient failures
3. **Optimize with ArrayPool**: Only if profiling shows GC pressure

For more information, see:
- [Polly Documentation](https://github.com/App-vNext/Polly)
- [OpenTelemetry .NET](https://github.com/open-telemetry/opentelemetry-dotnet)
- [ArrayPool Documentation](https://docs.microsoft.com/en-us/dotnet/api/system.buffers.arraypool-1)
