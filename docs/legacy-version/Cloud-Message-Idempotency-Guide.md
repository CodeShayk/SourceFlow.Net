# Cloud Message Idempotency Guide

## Overview

SourceFlow.Net provides flexible idempotency configuration for cloud-based deployments to handle duplicate messages in distributed systems. This guide explains how to configure idempotency services for AWS cloud integration, covering both in-memory and SQL-based approaches.

**Purpose**: Prevent duplicate message processing in distributed systems where at-least-once delivery guarantees can result in duplicate messages.

---

## Table of Contents

1. [Understanding Idempotency](#understanding-idempotency)
2. [Idempotency Approaches](#idempotency-approaches)
3. [In-Memory Idempotency](#in-memory-idempotency)
4. [SQL-Based Idempotency](#sql-based-idempotency)
5. [Configuration Methods](#configuration-methods)
6. [Fluent Builder API](#fluent-builder-api)
7. [Cloud Message Handling](#cloud-message-handling)
8. [Performance Considerations](#performance-considerations)
9. [Best Practices](#best-practices)
10. [Troubleshooting](#troubleshooting)

---

## Understanding Idempotency

### What is Idempotency?

Idempotency ensures that processing the same message multiple times produces the same result as processing it once. This is critical in distributed systems where:

- Cloud messaging services guarantee at-least-once delivery
- Network failures can cause message retries
- Multiple consumers might receive the same message

### How SourceFlow Implements Idempotency

```
Message Received
    ↓
Generate Idempotency Key
    ↓
Check if Already Processed
    ↓
If Duplicate → Skip Processing
If New → Process and Mark as Processed
```

### Idempotency Key Format

**Pattern**: `{CloudProvider}:{MessageType}:{MessageId}`

**Example**: `AWS:CreateOrderCommand:abc123-def456`

---

## Idempotency Approaches

SourceFlow provides two idempotency implementations:

### 1. In-Memory Idempotency

**Implementation**: `InMemoryIdempotencyService`

**Storage**: `ConcurrentDictionary<string, DateTime>`

**Use Cases**:
- Single-instance deployments
- Development and testing environments
- Local development with LocalStack

**Pros**:
- ✅ Zero configuration
- ✅ Fastest performance
- ✅ No external dependencies

**Cons**:
- ❌ Not shared across instances
- ❌ Lost on application restart
- ❌ Not suitable for production multi-instance deployments

### 2. SQL-Based Idempotency

**Implementation**: `EfIdempotencyService`

**Storage**: Database table (`IdempotencyRecords`)

**Use Cases**:
- Multi-instance production deployments
- Horizontal scaling scenarios
- High-availability configurations

**Pros**:
- ✅ Shared across all instances
- ✅ Survives application restarts
- ✅ Supports horizontal scaling
- ✅ Automatic cleanup

**Cons**:
- ⚠️ Requires database setup
- ⚠️ Slightly slower than in-memory (still fast)

---

## In-Memory Idempotency

### Default Behavior

By default, SourceFlow automatically registers an in-memory idempotency service when you configure AWS cloud integration.

### Configuration Example

```csharp
services.UseSourceFlow();

services.UseSourceFlowAws(
    options => { options.Region = RegionEndpoint.USEast1; },
    bus => bus
        .Send.Command<CreateOrderCommand>(q => q.Queue("orders.fifo"))
        .Listen.To.CommandQueue("orders.fifo"));

// InMemoryIdempotencyService registered automatically
```

### How It Works

```csharp
// Internal implementation (simplified)
public class InMemoryIdempotencyService : IIdempotencyService
{
    private readonly ConcurrentDictionary<string, DateTime> _processedMessages = new();

    public Task<bool> HasProcessedAsync(string idempotencyKey)
    {
        if (_processedMessages.TryGetValue(idempotencyKey, out var expiresAt))
        {
            return Task.FromResult(DateTime.UtcNow < expiresAt);
        }
        return Task.FromResult(false);
    }

    public Task MarkAsProcessedAsync(string idempotencyKey, TimeSpan ttl)
    {
        _processedMessages[idempotencyKey] = DateTime.UtcNow.Add(ttl);
        return Task.CompletedTask;
    }
}
```

### Automatic Cleanup

Expired entries are automatically removed from memory when checked.

---

## SQL-Based Idempotency

### Overview

The SQL-based idempotency service (`EfIdempotencyService`) provides distributed duplicate message detection using a database to track processed messages across multiple application instances.

### Key Components

#### 1. IdempotencyRecord Model

```csharp
public class IdempotencyRecord
{
    public string IdempotencyKey { get; set; }      // Primary key
    public DateTime ProcessedAt { get; set; }       // When first processed
    public DateTime ExpiresAt { get; set; }         // Expiration timestamp
    public string MessageType { get; set; }         // Optional: message type
    public string CloudProvider { get; set; }       // Optional: cloud provider
}
```

#### 2. IdempotencyDbContext

- Manages the `IdempotencyRecords` table
- Configures primary key on `IdempotencyKey`
- Adds index on `ExpiresAt` for efficient cleanup

#### 3. EfIdempotencyService

Implements `IIdempotencyService` with:
- **HasProcessedAsync**: Checks if message processed (not expired)
- **MarkAsProcessedAsync**: Records message as processed with TTL
- **RemoveAsync**: Deletes specific idempotency record
- **GetStatisticsAsync**: Returns processing statistics
- **CleanupExpiredRecordsAsync**: Batch cleanup of expired records

#### 4. IdempotencyCleanupService

Background hosted service that periodically cleans up expired records.

### Database Schema

```sql
CREATE TABLE IdempotencyRecords (
    IdempotencyKey NVARCHAR(500) PRIMARY KEY,
    ProcessedAt DATETIME2 NOT NULL,
    ExpiresAt DATETIME2 NOT NULL,
    MessageType NVARCHAR(500) NULL,
    CloudProvider NVARCHAR(50) NULL
);

CREATE INDEX IX_IdempotencyRecords_ExpiresAt 
    ON IdempotencyRecords(ExpiresAt);
```

### Installation

```bash
dotnet add package SourceFlow.Stores.EntityFramework
```

### Configuration

#### SQL Server (Default)

```csharp
services.AddSourceFlowIdempotency(
    connectionString: "Server=localhost;Database=SourceFlow;Trusted_Connection=True;",
    cleanupIntervalMinutes: 60); // Optional, defaults to 60 minutes
```

This method:
- Registers `IdempotencyDbContext` with SQL Server provider
- Registers `EfIdempotencyService` as scoped service
- Registers `IdempotencyCleanupService` as background hosted service
- Configures automatic cleanup at specified interval

#### Custom Database Provider

For PostgreSQL, MySQL, SQLite, or other EF Core providers:

```csharp
// PostgreSQL
services.AddSourceFlowIdempotencyWithCustomProvider(
    configureContext: options => options.UseNpgsql(connectionString),
    cleanupIntervalMinutes: 60);

// MySQL
services.AddSourceFlowIdempotencyWithCustomProvider(
    configureContext: options => options.UseMySql(
        connectionString, 
        ServerVersion.AutoDetect(connectionString)),
    cleanupIntervalMinutes: 60);

// SQLite
services.AddSourceFlowIdempotencyWithCustomProvider(
    configureContext: options => options.UseSqlite(connectionString),
    cleanupIntervalMinutes: 60);
```

### Features

#### Thread-Safe Duplicate Detection
- Uses database transactions for atomic operations
- Handles race conditions with upsert pattern
- Detects duplicate key violations across DB providers

#### Automatic Cleanup
- Background service runs at configurable intervals
- Batch deletion of expired records (1000 per cycle)
- Prevents unbounded table growth

#### Multi-Instance Support
- Shared database ensures consistency across instances
- No in-memory state required
- Scales horizontally with application

#### Statistics Tracking
- Total checks performed
- Duplicates detected
- Unique messages processed
- Current cache size

### Service Lifetime

The `EfIdempotencyService` is registered as **Scoped** to match the lifetime of cloud dispatchers:
- Command dispatchers are scoped (transaction boundaries)
- Event dispatchers are singleton but create scoped instances
- Scoped lifetime ensures proper DbContext lifecycle management

---

## Configuration Methods

### Method 1: Pre-Registration (Recommended)

Register the idempotency service before configuring AWS, and it will be automatically detected:

```csharp
services.UseSourceFlow();

// Register Entity Framework stores and SQL-based idempotency
services.AddSourceFlowEfStores(connectionString);
services.AddSourceFlowIdempotency(
    connectionString: connectionString,
    cleanupIntervalMinutes: 60);

// Configure AWS - will automatically use registered EF idempotency service
services.UseSourceFlowAws(
    options => { options.Region = RegionEndpoint.USEast1; },
    bus => bus
        .Send.Command<CreateOrderCommand>(q => q.Queue("orders.fifo"))
        .Listen.To.CommandQueue("orders.fifo"));
```

### Method 2: Explicit Configuration

Use the optional `configureIdempotency` parameter:

```csharp
services.UseSourceFlow();

// Register Entity Framework stores
services.AddSourceFlowEfStores(connectionString);

// Configure AWS with explicit idempotency configuration
services.UseSourceFlowAws(
    options => { options.Region = RegionEndpoint.USEast1; },
    bus => bus
        .Send.Command<CreateOrderCommand>(q => q.Queue("orders.fifo"))
        .Listen.To.CommandQueue("orders.fifo"),
    configureIdempotency: services =>
    {
        services.AddSourceFlowIdempotency(connectionString, cleanupIntervalMinutes: 60);
    });
```

### Method 3: Custom Implementation

Provide a custom idempotency implementation:

```csharp
services.UseSourceFlowAws(
    options => { options.Region = RegionEndpoint.USEast1; },
    bus => bus.Send.Command<CreateOrderCommand>(q => q.Queue("orders.fifo")),
    configureIdempotency: services =>
    {
        services.AddScoped<IIdempotencyService, MyCustomIdempotencyService>();
    });
```

### Registration Flow

1. **UseSourceFlowAws** is called with optional `configureIdempotency` parameter
2. If `configureIdempotency` parameter is provided, it's executed to register the idempotency service
3. If `configureIdempotency` is null, checks if `IIdempotencyService` is already registered
4. If not registered, registers `InMemoryIdempotencyService` as default

---

## Fluent Builder API

SourceFlow provides a fluent `IdempotencyConfigurationBuilder` for more expressive configuration.

### Using the Builder with Entity Framework

**Important**: The `UseEFIdempotency` method requires the `SourceFlow.Stores.EntityFramework` package. The builder uses reflection to avoid a direct dependency in the core package.

```csharp
// First, ensure the package is installed:
// dotnet add package SourceFlow.Stores.EntityFramework

var idempotencyBuilder = new IdempotencyConfigurationBuilder()
    .UseEFIdempotency(connectionString, cleanupIntervalMinutes: 60);

// Apply configuration to service collection
idempotencyBuilder.Build(services);

// Then configure cloud provider
services.UseSourceFlowAws(
    options => { options.Region = RegionEndpoint.USEast1; },
    bus => bus.Send.Command<CreateOrderCommand>(q => q.Queue("orders.fifo")));
```

If the EntityFramework package is not installed, you'll receive a clear error message:
```
SourceFlow.Stores.EntityFramework package is not installed. 
Install it using: dotnet add package SourceFlow.Stores.EntityFramework
```

### Using the Builder with In-Memory

```csharp
var idempotencyBuilder = new IdempotencyConfigurationBuilder()
    .UseInMemory();

idempotencyBuilder.Build(services);
```

### Using the Builder with Custom Implementation

```csharp
// With type parameter
var idempotencyBuilder = new IdempotencyConfigurationBuilder()
    .UseCustom<MyCustomIdempotencyService>();

// Or with factory function
var idempotencyBuilder = new IdempotencyConfigurationBuilder()
    .UseCustom(provider => 
    {
        var logger = provider.GetRequiredService<ILogger<MyCustomIdempotencyService>>();
        return new MyCustomIdempotencyService(logger);
    });

idempotencyBuilder.Build(services);
```

### Builder Methods

| Method | Description | Use Case |
|--------|-------------|----------|
| `UseEFIdempotency(connectionString, cleanupIntervalMinutes)` | Configure Entity Framework-based idempotency (uses reflection) | Multi-instance production deployments |
| `UseInMemory()` | Configure in-memory idempotency | Single-instance or development environments |
| `UseCustom<TImplementation>()` | Register custom implementation by type | Custom idempotency logic with DI |
| `UseCustom(factory)` | Register custom implementation with factory | Custom idempotency with complex initialization |
| `Build(services)` | Apply configuration to service collection (uses TryAddScoped) | Final step to register services |

### Builder Implementation Details

- **Reflection-Based EF Integration**: `UseEFIdempotency` uses reflection to call `AddSourceFlowIdempotency` from the EntityFramework package
- **Lazy Registration**: The `Build` method only registers services if no configuration was set, using `TryAddScoped`
- **Error Handling**: Clear error messages guide users when required packages are missing
- **Service Lifetime**: All idempotency services are registered as Scoped to match dispatcher lifetimes

### Builder Benefits

- **Explicit Configuration**: Clear, readable idempotency setup
- **Reusable**: Create builder instances for different environments
- **Testable**: Easy to mock and test configuration logic
- **Type-Safe**: Compile-time validation of configuration
- **Flexible**: Mix and match with direct service registration

---

## Cloud Message Handling

### Integration with AWS Dispatchers

#### AwsSqsCommandListener

```csharp
// In AwsSqsCommandListener
var idempotencyKey = GenerateIdempotencyKey(message);

if (await idempotencyService.HasProcessedAsync(idempotencyKey))
{
    // Duplicate detected - skip processing
    await DeleteMessage(message);
    return;
}

// Process message
await commandBus.Publish(command);

// Mark as processed
await idempotencyService.MarkAsProcessedAsync(idempotencyKey, ttl);
```

### Message TTL Configuration

**Default TTL**: 5 minutes

**Configurable per message type**:
```csharp
// Short TTL for high-frequency messages
await idempotencyService.MarkAsProcessedAsync(key, TimeSpan.FromMinutes(2));

// Longer TTL for critical operations
await idempotencyService.MarkAsProcessedAsync(key, TimeSpan.FromMinutes(15));
```

### Cleanup Process

The SQL-based idempotency service includes a background cleanup service that:
- Runs at configurable intervals (default: 60 minutes)
- Deletes expired records in batches (1000 per cycle)
- Prevents unbounded table growth
- Runs independently without blocking message processing

---

## Performance Considerations

### In-Memory Performance

- **Lookup**: O(1) dictionary lookup
- **Memory**: Minimal overhead per message
- **Cleanup**: Automatic on access

### SQL-Based Performance

#### Indexes
- Primary key on `IdempotencyKey` for fast lookups
- Index on `ExpiresAt` for efficient cleanup queries

#### Cleanup Strategy
- Batch deletion (1000 records per cycle)
- Configurable cleanup interval
- Runs in background without blocking message processing

#### Connection Pooling
- Uses Entity Framework Core connection pooling
- Scoped lifetime matches dispatcher lifetime
- Efficient resource utilization

### Performance Comparison

| Operation | In-Memory | SQL-Based |
|-----------|-----------|-----------|
| **Lookup** | < 1 ms | 1-5 ms |
| **Insert** | < 1 ms | 2-10 ms |
| **Cleanup** | Automatic | Background (60 min) |
| **Throughput** | 100k+ msg/sec | 10k+ msg/sec |

---

## Best Practices

### Development Environment

Use in-memory idempotency for simplicity:

```csharp
services.UseSourceFlowAws(
    options => { options.Region = RegionEndpoint.USEast1; },
    bus => bus.Send.Command<CreateOrderCommand>(q => q.Queue("orders.fifo")));
// In-memory idempotency registered automatically
```

### Production Environment

Use SQL-based idempotency for reliability:

```csharp
services.AddSourceFlowEfStores(connectionString);
services.AddSourceFlowIdempotency(connectionString, cleanupIntervalMinutes: 60);

services.UseSourceFlowAws(
    options => { options.Region = RegionEndpoint.USEast1; },
    bus => bus.Send.Command<CreateOrderCommand>(q => q.Queue("orders.fifo")));
```

### Configuration Management

Use environment-specific configuration:

```csharp
var connectionString = configuration.GetConnectionString("SourceFlow");
var cleanupInterval = configuration.GetValue<int>("SourceFlow:IdempotencyCleanupMinutes", 60);

if (environment.IsProduction())
{
    services.AddSourceFlowIdempotency(connectionString, cleanupInterval);
}
// Development uses in-memory by default
```

### Database Best Practices

1. **Connection String**: Use the same database as your command/entity stores for consistency
2. **Cleanup Interval**: Set based on your TTL values (typically 1-2 hours)
3. **TTL Values**: Match your message retention policies (typically 5-15 minutes)
4. **Monitoring**: Track statistics to understand duplicate message rates
5. **Database Maintenance**: Ensure indexes are maintained for optimal performance

---

## Troubleshooting

### Issue: High Duplicate Detection Rate

**Symptoms**: Many messages marked as duplicates

**Solutions**:
- Check message TTL values (should match your processing time)
- Verify cloud provider retry settings
- Review message deduplication configuration (SQS ContentBasedDeduplication)
- Check for application restarts causing message reprocessing

### Issue: Cleanup Not Running

**Symptoms**: IdempotencyRecords table growing unbounded

**Solutions**:
- Verify background service is registered (`IdempotencyCleanupService`)
- Check application logs for cleanup errors
- Ensure database permissions allow DELETE operations
- Verify cleanup interval is appropriate
- Check that the hosted service is starting correctly

### Issue: Performance Degradation

**Symptoms**: Slow message processing

**Solutions**:
- Verify indexes exist on `IdempotencyKey` and `ExpiresAt`
- Consider increasing cleanup interval
- Monitor database connection pool usage
- Check for database locks or contention
- Review query execution plans

### Issue: Duplicate Processing After Restart

**Symptoms**: Messages processed again after application restart

**Expected Behavior**:
- **In-Memory**: This is expected - state is lost on restart
- **SQL-Based**: Should not happen - check database connectivity

**Solutions**:
- Use SQL-based idempotency for production
- Ensure database is accessible during startup
- Verify connection string is correct

### Issue: Migration from In-Memory to SQL-Based

**Steps**:
1. Add the SQL-based service registration:
```csharp
services.AddSourceFlowIdempotency(connectionString);
```

2. Ensure database exists and is accessible

3. The `IdempotencyRecords` table will be created automatically on first use

4. No code changes required in dispatchers or listeners

5. Deploy to all instances simultaneously to avoid mixed behavior

---

## Comparison Matrix

| Feature | In-Memory | SQL-Based |
|---------|-----------|-----------|
| **Single Instance** | ✅ Excellent | ✅ Works |
| **Multi-Instance** | ❌ Not supported | ✅ Excellent |
| **Performance** | ⚡ Fastest | 🔥 Fast |
| **Persistence** | ❌ Lost on restart | ✅ Survives restarts |
| **Cleanup** | ✅ Automatic (memory) | ✅ Automatic (background service) |
| **Setup Complexity** | ✅ Zero config | ⚠️ Requires database |
| **Scalability** | ❌ Single instance only | ✅ Horizontal scaling |
| **Database Required** | ❌ No | ✅ Yes |
| **Package Required** | ❌ No | ✅ SourceFlow.Stores.EntityFramework |

---

## Related Documentation

- [AWS Cloud Architecture](Architecture/07-AWS-Cloud-Architecture.md)
- [AWS Cloud Extension Package](SourceFlow.Cloud.AWS-README.md)
- [Entity Framework Stores](SourceFlow.Stores.EntityFramework-README.md)
- [Cloud Integration Testing](Cloud-Integration-Testing.md)

---

**Document Version**: 2.0  
**Last Updated**: 2026-03-04  
**Status**: Complete
