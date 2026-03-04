# Idempotency Configuration Guide

## Overview

SourceFlow.Net provides flexible idempotency configuration for cloud-based deployments to handle duplicate messages in distributed systems. This guide explains how to configure idempotency services when using AWS or Azure cloud extensions.

## Default Behavior (In-Memory)

By default, SourceFlow automatically registers an in-memory idempotency service when you configure AWS or Azure cloud integration. This is suitable for single-instance deployments.

### AWS Example

```csharp
services.UseSourceFlow();

services.UseSourceFlowAws(
    options => { options.Region = RegionEndpoint.USEast1; },
    bus => bus
        .Send.Command<CreateOrderCommand>(q => q.Queue("orders.fifo"))
        .Listen.To.CommandQueue("orders.fifo"));
```

### Azure Example

```csharp
services.UseSourceFlow();

services.UseSourceFlowAzure(
    options => 
    { 
        options.FullyQualifiedNamespace = "myservicebus.servicebus.windows.net";
        options.UseManagedIdentity = true;
    },
    bus => bus
        .Send.Command<CreateOrderCommand>(q => q.Queue("orders"))
        .Listen.To.CommandQueue("orders"));
```

## Multi-Instance Deployment (SQL-Based)

For production deployments with multiple instances, use the SQL-based idempotency service to ensure duplicate detection across all instances.

### Step 1: Install Required Package

```bash
dotnet add package SourceFlow.Stores.EntityFramework
```

### Step 2: Register SQL-Based Idempotency

#### AWS Configuration (Recommended Approach)

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

#### AWS Configuration (Alternative Approach)

Use the optional `configureIdempotency` parameter to explicitly configure the idempotency service:

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

#### Azure Configuration

```csharp
services.UseSourceFlow();

// Register Entity Framework stores and SQL-based idempotency
services.AddSourceFlowEfStores(connectionString);
services.AddSourceFlowIdempotency(
    connectionString: connectionString,
    cleanupIntervalMinutes: 60);

// Configure Azure - will use registered EF idempotency service
services.UseSourceFlowAzure(
    options => 
    { 
        options.FullyQualifiedNamespace = "myservicebus.servicebus.windows.net";
        options.UseManagedIdentity = true;
    },
    bus => bus
        .Send.Command<CreateOrderCommand>(q => q.Queue("orders"))
        .Listen.To.CommandQueue("orders"));
```

### Step 3: Database Setup

The `IdempotencyRecords` table will be created automatically on first use. Alternatively, you can create it manually:

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

## Custom Idempotency Service

You can provide a custom idempotency implementation using the optional `configureIdempotency` parameter available in AWS (and coming soon to Azure).

### AWS Example

```csharp
services.UseSourceFlowAws(
    options => { options.Region = RegionEndpoint.USEast1; },
    bus => bus.Send.Command<CreateOrderCommand>(q => q.Queue("orders.fifo")),
    configureIdempotency: services =>
    {
        services.AddScoped<IIdempotencyService, MyCustomIdempotencyService>();
    });
```

### Azure Example (Coming Soon)

Azure will support the `configureIdempotency` parameter in a future release. For now, register the idempotency service before calling `UseSourceFlowAzure`:

```csharp
services.AddScoped<IIdempotencyService, MyCustomIdempotencyService>();

services.UseSourceFlowAzure(
    options => { options.FullyQualifiedNamespace = "myservicebus.servicebus.windows.net"; },
    bus => bus.Send.Command<CreateOrderCommand>(q => q.Queue("orders")));
```

## Fluent Builder API (Alternative Configuration)

SourceFlow provides a fluent `IdempotencyConfigurationBuilder` for more expressive configuration. This builder is particularly useful when you want to configure idempotency independently of cloud provider setup.

### Using the Builder with Entity Framework

**Important**: The `UseEFIdempotency` method requires the `SourceFlow.Stores.EntityFramework` package to be installed. The builder uses reflection to call the registration method, avoiding a direct dependency in the core package.

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
| `UseEFIdempotency(connectionString, cleanupIntervalMinutes)` | Configure Entity Framework-based idempotency (uses reflection to avoid direct dependency) | Multi-instance production deployments |
| `UseInMemory()` | Configure in-memory idempotency | Single-instance or development environments |
| `UseCustom<TImplementation>()` | Register custom implementation by type | Custom idempotency logic with DI |
| `UseCustom(factory)` | Register custom implementation with factory | Custom idempotency with complex initialization |
| `Build(services)` | Apply configuration to service collection (uses TryAddScoped for default) | Final step to register services |

### Builder Implementation Details

- **Reflection-Based EF Integration**: `UseEFIdempotency` uses reflection to call `AddSourceFlowIdempotency` from the EntityFramework package, avoiding a direct dependency in the core SourceFlow package
- **Lazy Registration**: The `Build` method only registers services if no configuration was set, using `TryAddScoped` to avoid overwriting existing registrations
- **Error Handling**: Clear error messages guide users when required packages are missing or methods cannot be found
- **Service Lifetime**: All idempotency services are registered as Scoped to match dispatcher lifetimes

### Builder Benefits

- **Explicit Configuration**: Clear, readable idempotency setup
- **Reusable**: Create builder instances for different environments
- **Testable**: Easy to mock and test configuration logic
- **Type-Safe**: Compile-time validation of configuration
- **Flexible**: Mix and match with direct service registration

## Configuration Options

### SQL-Based Idempotency Options

```csharp
services.AddSourceFlowIdempotency(
    connectionString: "Server=...;Database=...;",
    cleanupIntervalMinutes: 60);  // Cleanup interval (default: 60 minutes)
```

### Custom Database Provider

For databases other than SQL Server:

```csharp
services.AddSourceFlowIdempotencyWithCustomProvider(
    configureContext: options => options.UseNpgsql(connectionString),
    cleanupIntervalMinutes: 60);
```

## How It Works

### Registration Flow (AWS)

1. **UseSourceFlowAws** is called with optional `configureIdempotency` parameter
2. If `configureIdempotency` parameter is provided, it's executed to register the idempotency service
3. If `configureIdempotency` is null, checks if `IIdempotencyService` is already registered
4. If not registered, registers `InMemoryIdempotencyService` as default

### Registration Flow (Azure)

1. **UseSourceFlowAzure** is called
2. Checks if `IIdempotencyService` is already registered
3. If not registered, registers `InMemoryIdempotencyService` as default

**Note**: Azure will support the `configureIdempotency` parameter in a future release.

### Service Lifetime

- **In-Memory**: Scoped (per request/message processing)
- **SQL-Based**: Scoped (per request/message processing)
- **Custom**: Depends on your registration

### Cleanup Process

The SQL-based idempotency service includes a background cleanup service that:
- Runs at configurable intervals (default: 60 minutes)
- Deletes expired records in batches (1000 per cycle)
- Prevents unbounded table growth
- Runs independently without blocking message processing

## Comparison

| Feature | In-Memory | SQL-Based |
|---------|-----------|-----------|
| **Single Instance** | ✅ Excellent | ✅ Works |
| **Multi-Instance** | ❌ Not supported | ✅ Excellent |
| **Performance** | ⚡ Fastest | 🔥 Fast |
| **Persistence** | ❌ Lost on restart | ✅ Survives restarts |
| **Cleanup** | ✅ Automatic (memory) | ✅ Automatic (background service) |
| **Setup Complexity** | ✅ Zero config | ⚠️ Requires database |
| **Scalability** | ❌ Single instance only | ✅ Horizontal scaling |

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

## Troubleshooting

### Issue: High Duplicate Detection Rate

**Symptoms**: Many messages marked as duplicates

**Solutions**:
- Check message TTL values (should match your processing time)
- Verify cloud provider retry settings
- Review message deduplication configuration (SQS, Service Bus)

### Issue: Cleanup Not Running

**Symptoms**: IdempotencyRecords table growing unbounded

**Solutions**:
- Verify background service is registered
- Check application logs for cleanup errors
- Ensure database permissions allow DELETE operations
- Verify cleanup interval is appropriate

### Issue: Performance Degradation

**Symptoms**: Slow message processing

**Solutions**:
- Verify indexes exist on `IdempotencyKey` and `ExpiresAt`
- Consider increasing cleanup interval
- Monitor database connection pool usage
- Check for database locks or contention

## Related Documentation

- [SQL-Based Idempotency Service](SQL-Based-Idempotency-Service.md)
- [AWS Cloud Integration](../src/SourceFlow.Cloud.AWS/README.md)
- [Azure Cloud Integration](../src/SourceFlow.Cloud.Azure/README.md)
- [Entity Framework Stores](SourceFlow.Stores.EntityFramework-README.md)
