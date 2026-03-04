# SourceFlow.Stores.EntityFramework

Entity Framework Core persistence provider for SourceFlow.Net with support for SQL Server and configurable connection strings per store type.

## Features

- **Complete Store Implementations**: ICommandStore, IEntityStore, and IViewModelStore
- **Idempotency Service**: SQL-based duplicate message detection for multi-instance deployments
- **Flexible Configuration**: Separate or shared connection strings per store type
- **SQL Server Support**: Built-in SQL Server database provider
- **Resilience Policies**: Polly-based retry and circuit breaker patterns
- **Observability**: OpenTelemetry instrumentation for database operations
- **Multi-Framework Support**: .NET 8.0, .NET 9.0, .NET 10.0

## Installation

```bash
# Install the core package
dotnet add package SourceFlow.Net

# Install the Entity Framework provider
dotnet add package SourceFlow.Stores.EntityFramework
```

## Quick Start

### 1. Configure Connection Strings

Add connection strings to your `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "CommandStore": "Server=localhost;Database=SourceFlowCommands;Trusted_Connection=True;",
    "EntityStore": "Server=localhost;Database=SourceFlowEntities;Trusted_Connection=True;",
    "ViewModelStore": "Server=localhost;Database=SourceFlowViews;Trusted_Connection=True;"
  }
}
```

Or use a single shared connection string:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost;Database=SourceFlow;Trusted_Connection=True;"
  }
}
```

### 2. Register Services

```csharp
services.AddSourceFlowStores(configuration, options =>
{
    // Use separate databases for each store
    options.UseCommandStore("CommandStore");
    options.UseEntityStore("EntityStore");
    options.UseViewModelStore("ViewModelStore");

    // Or use a single shared database
    // options.UseSharedConnectionString("DefaultConnection");
});
```

### 3. Apply Migrations

The provider automatically creates the necessary database schema when you run your application. For production scenarios, generate and apply migrations:

```bash
dotnet ef migrations add InitialCreate --context CommandStoreContext
dotnet ef database update --context CommandStoreContext
```

## Configuration Options

### Separate Databases

Configure different databases for commands, entities, and view models:

```csharp
services.AddSourceFlowStores(configuration, options =>
{
    options.UseCommandStore("CommandStoreConnection");
    options.UseEntityStore("EntityStoreConnection");
    options.UseViewModelStore("ViewModelStoreConnection");
});
```

### Shared Database

Use a single database for all stores:

```csharp
services.AddSourceFlowStores(configuration, options =>
{
    options.UseSharedConnectionString("DefaultConnection");
});
```

### Custom DbContext Options

Apply additional EF Core configuration:

```csharp
services.AddSourceFlowStores(configuration, options =>
{
    options.UseCommandStore("CommandStore", dbOptions =>
    {
        dbOptions.EnableSensitiveDataLogging();
        dbOptions.EnableDetailedErrors();
    });
});
```

## Resilience

The provider includes built-in Polly resilience policies for:
- Transient error retry with exponential backoff
- Circuit breaker for database failures
- Automatic reconnection handling

## Idempotency Service

The Entity Framework provider includes `EfIdempotencyService`, a SQL-based implementation of `IIdempotencyService` designed for multi-instance deployments where in-memory idempotency tracking is insufficient.

### Features

- **Thread-Safe Duplicate Detection**: Uses database transactions to ensure consistency across multiple application instances
- **Automatic Expiration**: Records expire based on configurable TTL (Time To Live)
- **Background Cleanup**: Automatic periodic cleanup of expired records
- **Statistics**: Track total checks, duplicates detected, and cache size
- **Database Agnostic**: Support for SQL Server, PostgreSQL, MySQL, SQLite, and other EF Core providers

### Configuration

#### SQL Server (Default)

Register the idempotency service with automatic cleanup:

```csharp
services.AddSourceFlowIdempotency(
    connectionString: configuration.GetConnectionString("IdempotencyStore"),
    cleanupIntervalMinutes: 60); // Optional, defaults to 60 minutes
```

#### Custom Database Provider

Use PostgreSQL, MySQL, SQLite, or any other EF Core provider:

```csharp
// PostgreSQL
services.AddSourceFlowIdempotencyWithCustomProvider(
    configureContext: options => options.UseNpgsql(connectionString),
    cleanupIntervalMinutes: 60);

// MySQL
services.AddSourceFlowIdempotencyWithCustomProvider(
    configureContext: options => options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)),
    cleanupIntervalMinutes: 60);

// SQLite
services.AddSourceFlowIdempotencyWithCustomProvider(
    configureContext: options => options.UseSqlite(connectionString),
    cleanupIntervalMinutes: 60);
```

#### Manual Registration (Advanced)

For more control over the registration:

```csharp
services.AddDbContext<IdempotencyDbContext>(options =>
    options.UseSqlServer(configuration.GetConnectionString("IdempotencyStore")));

services.AddScoped<IIdempotencyService, EfIdempotencyService>();

// Optional: Register background cleanup service
services.AddHostedService<IdempotencyCleanupService>(provider =>
    new IdempotencyCleanupService(provider, TimeSpan.FromMinutes(60)));
```

### Database Schema

The service uses a single table with the following structure:

```sql
CREATE TABLE IdempotencyRecords (
    IdempotencyKey NVARCHAR(500) PRIMARY KEY,
    ProcessedAt DATETIME2 NOT NULL,
    ExpiresAt DATETIME2 NOT NULL
);

CREATE INDEX IX_IdempotencyRecords_ExpiresAt ON IdempotencyRecords(ExpiresAt);
```

The schema is automatically created when you run migrations or when the application starts (if auto-migration is enabled).

### Usage

The service is automatically used by cloud dispatchers when registered:

```csharp
// Check if message was already processed
if (await idempotencyService.HasProcessedAsync(messageId))
{
    // Skip duplicate message
    return;
}

// Process message...

// Mark as processed with 24-hour TTL
await idempotencyService.MarkAsProcessedAsync(messageId, TimeSpan.FromHours(24));
```

### Cleanup

The `AddSourceFlowIdempotency` and `AddSourceFlowIdempotencyWithCustomProvider` methods automatically register a background service (`IdempotencyCleanupService`) that periodically cleans up expired records.

**Default Behavior:**
- Cleanup runs every 60 minutes (configurable)
- Processes up to 1000 expired records per batch
- Runs as a hosted background service

**Custom Cleanup Interval:**

```csharp
services.AddSourceFlowIdempotency(
    connectionString: configuration.GetConnectionString("IdempotencyStore"),
    cleanupIntervalMinutes: 30); // Run cleanup every 30 minutes
```

**Manual Cleanup (Advanced):**

If you need to trigger cleanup manually or implement custom cleanup logic:

```csharp
public class CustomCleanupJob : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = _serviceProvider.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<EfIdempotencyService>();
            
            await service.CleanupExpiredRecordsAsync(stoppingToken);
            
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
```

### When to Use

- **Multi-Instance Deployments**: When running multiple application instances that process the same message queues
- **Distributed Systems**: When messages can be delivered more than once (at-least-once delivery)
- **Cloud Messaging**: When using AWS SQS, Azure Service Bus, or other cloud message queues

For single-instance deployments, consider using `InMemoryIdempotencyService` from the core framework for better performance.

## Documentation

- [Full Documentation](https://github.com/CodeShayk/SourceFlow.Net/wiki)
- [GitHub Repository](https://github.com/CodeShayk/SourceFlow.Net)
- [Report Issues](https://github.com/CodeShayk/SourceFlow.Net/issues)
- [Release Notes](https://github.com/CodeShayk/SourceFlow.Net/blob/master/CHANGELOG.md)

## Support

- **Issues**: [GitHub Issues](https://github.com/CodeShayk/SourceFlow.Net/issues/new/choose)
- **Discussions**: [GitHub Discussions](https://github.com/CodeShayk/SourceFlow.Net/discussions)

## License

This project is licensed under the [MIT License](https://github.com/CodeShayk/SourceFlow.Net/blob/master/LICENSE).
