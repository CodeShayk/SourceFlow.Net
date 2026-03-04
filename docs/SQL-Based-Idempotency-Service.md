# SQL-Based Idempotency Service

## Overview

The SQL-based idempotency service (`EfIdempotencyService`) provides distributed duplicate message detection for multi-instance deployments of SourceFlow applications. Unlike the in-memory implementation, this service uses a database to track processed messages, ensuring idempotency across multiple application instances.

## Key Components

### 1. IdempotencyRecord Model
Located in `src/SourceFlow.Stores.EntityFramework/Models/IdempotencyRecord.cs`

```csharp
public class IdempotencyRecord
{
    public string IdempotencyKey { get; set; }      // Primary key
    public DateTime ProcessedAt { get; set; }       // When first processed
    public DateTime ExpiresAt { get; set; }         // Expiration timestamp
}
```

### 2. IdempotencyDbContext
Located in `src/SourceFlow.Stores.EntityFramework/IdempotencyDbContext.cs`

- Manages the `IdempotencyRecords` table
- Configures primary key on `IdempotencyKey`
- Adds index on `ExpiresAt` for efficient cleanup

### 3. EfIdempotencyService
Located in `src/SourceFlow.Stores.EntityFramework/Services/EfIdempotencyService.cs`

Implements `IIdempotencyService` with the following methods:

- **HasProcessedAsync**: Checks if a message has been processed (not expired)
- **MarkAsProcessedAsync**: Records a message as processed with TTL
- **RemoveAsync**: Deletes a specific idempotency record
- **GetStatisticsAsync**: Returns processing statistics
- **CleanupExpiredRecordsAsync**: Batch cleanup of expired records

### 4. IdempotencyCleanupService
Located in `src/SourceFlow.Stores.EntityFramework/Services/IdempotencyCleanupService.cs`

Background hosted service that periodically cleans up expired idempotency records.

## Registration

### Quick Start

The simplest way to register the idempotency service is using the extension methods that handle all configuration automatically:

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

### Manual Registration (Advanced)

For more control over the registration process:

```csharp
// Register DbContext
services.AddDbContext<IdempotencyDbContext>(options =>
    options.UseSqlServer(connectionString));

// Register service as Scoped (matches cloud dispatcher lifetime)
services.AddScoped<IIdempotencyService, EfIdempotencyService>();

// Optional: Register background cleanup service
services.AddHostedService<IdempotencyCleanupService>(provider =>
    new IdempotencyCleanupService(
        provider,
        TimeSpan.FromMinutes(60)));
```

### Service Lifetime

The `EfIdempotencyService` is registered as **Scoped** to match the lifetime of cloud dispatchers:
- Command dispatchers are scoped (transaction boundaries)
- Event dispatchers are singleton but create scoped instances
- Scoped lifetime ensures proper DbContext lifecycle management

## Features

### Thread-Safe Duplicate Detection
- Uses database transactions for atomic operations
- Handles race conditions with upsert pattern
- Detects duplicate key violations across DB providers

### Automatic Cleanup
- Background service runs at configurable intervals
- Batch deletion of expired records (1000 per cycle)
- Prevents unbounded table growth

### Multi-Instance Support
- Shared database ensures consistency across instances
- No in-memory state required
- Scales horizontally with application

### Statistics Tracking
- Total checks performed
- Duplicates detected
- Unique messages processed
- Current cache size

## Database Schema

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

## Usage Example

```csharp
// Startup.cs or Program.cs
services.AddSourceFlowEfStores(connectionString);
services.AddSourceFlowIdempotency(
    connectionString: connectionString,
    cleanupIntervalMinutes: 60);

services.UseSourceFlowAws(
    options => { options.Region = RegionEndpoint.USEast1; },
    bus => bus
        .Send.Command<CreateOrderCommand>(q => q.Queue("orders.fifo"))
        .Listen.To.CommandQueue("orders.fifo"));
```

## Testing

Unit tests are located in `tests/SourceFlow.Net.EntityFramework.Tests/Unit/EfIdempotencyServiceTests.cs`

Tests cover:
- Key existence checks
- Record creation and updates
- Expiration handling
- Cleanup operations
- Statistics tracking

Run tests:
```bash
dotnet test tests/SourceFlow.Net.EntityFramework.Tests/
```

## Performance Considerations

### Indexes
- Primary key on `IdempotencyKey` for fast lookups
- Index on `ExpiresAt` for efficient cleanup queries

### Cleanup Strategy
- Batch deletion (1000 records per cycle)
- Configurable cleanup interval
- Runs in background without blocking message processing

### Connection Pooling
- Uses Entity Framework Core connection pooling
- Scoped lifetime matches dispatcher lifetime
- Efficient resource utilization

## Migration from InMemoryIdempotencyService

1. Add the SQL-based service registration:
```csharp
services.AddSourceFlowIdempotency(connectionString);
```

2. Ensure database exists and is accessible

3. The `IdempotencyRecords` table will be created automatically on first use

4. No code changes required in dispatchers or listeners

## Best Practices

1. **Connection String**: Use the same database as your command/entity stores for consistency
2. **Cleanup Interval**: Set based on your TTL values (typically 1-2 hours)
3. **TTL Values**: Match your message retention policies (typically 5-15 minutes)
4. **Monitoring**: Track statistics to understand duplicate message rates
5. **Database Maintenance**: Ensure indexes are maintained for optimal performance

## Troubleshooting

### High Duplicate Rates
- Check for message retry logic in cloud providers
- Verify TTL values are appropriate
- Review message deduplication settings (SQS, Service Bus)

### Cleanup Not Running
- Verify background service is registered
- Check application logs for cleanup errors
- Ensure database permissions allow DELETE operations

### Performance Issues
- Verify indexes exist on `IdempotencyKey` and `ExpiresAt`
- Consider increasing cleanup interval
- Monitor database connection pool usage
