# SourceFlow Cloud Core

**Project**: `src/SourceFlow/Cloud/` (consolidated into core framework)  
**Purpose**: Shared cloud functionality and patterns for AWS and Azure extensions

**Note**: As of the latest architecture update, Cloud.Core functionality has been consolidated into the main SourceFlow project under the `Cloud/` namespace. This simplifies dependencies and reduces the number of separate packages.

## Core Functionality

### Bus Configuration System
- **`BusConfiguration`** - Code-first fluent API for routing configuration
- **`BusConfigurationBuilder`** - Entry point for building bus configurations
- **`IBusBootstrapConfiguration`** - Interface for bootstrapper integration
- **`ICommandRoutingConfiguration`** - Command routing abstraction
- **`IEventRoutingConfiguration`** - Event routing abstraction
- **Fluent API Sections** - Send, Raise, Listen, Subscribe for intuitive configuration

### Resilience Patterns
- **`ICircuitBreaker`** - Circuit breaker pattern implementation
- **`CircuitBreaker`** - Configurable fault tolerance with state management
- **`CircuitBreakerOptions`** - Configuration for failure thresholds and timeouts
- **`CircuitBreakerOpenException`** - Exception thrown when circuit is open
- **`CircuitBreakerStateChangedEventArgs`** - Event args for state transitions
- **State Management** - Open, Closed, Half-Open states with automatic transitions

### Security Infrastructure
- **`IMessageEncryption`** - Abstraction for message encryption/decryption
- **`SensitiveDataAttribute`** - Marks properties for encryption
- **`SensitiveDataMasker`** - Automatic masking of sensitive data in logs
- **`EncryptionOptions`** - Configuration for encryption providers

### Dead Letter Processing
- **`IDeadLetterProcessor`** - Interface for handling failed messages
- **`IDeadLetterStore`** - Persistence for failed message analysis
- **`DeadLetterRecord`** - Model for failed message metadata
- **`InMemoryDeadLetterStore`** - Default in-memory implementation

### Observability Infrastructure
- **`CloudActivitySource`** - OpenTelemetry activity source for cloud operations
- **`CloudMetrics`** - Standard metrics for cloud messaging
- **`CloudTelemetry`** - Centralized telemetry management

## Circuit Breaker Pattern

### Configuration
```csharp
var options = new CircuitBreakerOptions
{
    FailureThreshold = 5,           // Failures before opening
    SuccessThreshold = 3,           // Successes to close from half-open
    Timeout = TimeSpan.FromMinutes(1), // Time before half-open attempt
    SamplingDuration = TimeSpan.FromSeconds(30) // Failure rate calculation window
};
```

### Usage Pattern
```csharp
public class CloudService
{
    private readonly ICircuitBreaker _circuitBreaker;
    
    public async Task<T> CallExternalService<T>()
    {
        return await _circuitBreaker.ExecuteAsync(async () =>
        {
            // External service call that might fail
            return await externalService.CallAsync();
        });
    }
}
```

### State Management
- **Closed** - Normal operation, failures counted
- **Open** - All calls rejected immediately, timeout period active
- **Half-Open** - Test calls allowed to check service recovery

## Security Features

### Message Encryption
```csharp
public interface IMessageEncryption
{
    Task<string> EncryptAsync(string plaintext);
    Task<string> DecryptAsync(string ciphertext);
    Task<byte[]> EncryptAsync(byte[] plaintext);
    Task<byte[]> DecryptAsync(byte[] ciphertext);
}
```

### Sensitive Data Handling
```csharp
public class UserCommand
{
    public string Username { get; set; }
    
    [SensitiveData]
    public string Password { get; set; }  // Automatically encrypted/masked
    
    [SensitiveData]
    public string CreditCard { get; set; } // Automatically encrypted/masked
}
```

### Data Masking
- **Automatic Masking** - Sensitive properties masked in logs
- **Configurable Patterns** - Custom masking rules
- **Performance Optimized** - Minimal overhead for non-sensitive data

## Dead Letter Management

### Dead Letter Record
```csharp
public class DeadLetterRecord
{
    public string Id { get; set; }
    public string MessageId { get; set; }
    public string MessageType { get; set; }
    public string MessageBody { get; set; }
    public string ErrorMessage { get; set; }
    public string StackTrace { get; set; }
    public int RetryCount { get; set; }
    public DateTime FirstFailure { get; set; }
    public DateTime LastFailure { get; set; }
    public Dictionary<string, string> Properties { get; set; }
}
```

### Processing Interface
```csharp
public interface IDeadLetterProcessor
{
    Task ProcessAsync(DeadLetterRecord record);
    Task<bool> CanRetryAsync(DeadLetterRecord record);
    Task RequeueAsync(DeadLetterRecord record);
    Task ArchiveAsync(DeadLetterRecord record);
}
```

## Observability Infrastructure

### Activity Source
```csharp
public static class CloudActivitySource
{
    public static readonly ActivitySource Instance = new("SourceFlow.Cloud");
    
    public static Activity? StartActivity(string name, ActivityKind kind = ActivityKind.Internal)
    {
        return Instance.StartActivity(name, kind);
    }
}
```

### Standard Metrics
- **Message Processing** - Throughput, latency, error rates
- **Circuit Breaker** - State changes, failure rates, recovery times
- **Dead Letter** - Failed message counts, retry attempts
- **Encryption** - Encryption/decryption operations, key usage

### Telemetry Integration
```csharp
public class CloudTelemetry
{
    public static void RecordMessageProcessed(string messageType, TimeSpan duration);
    public static void RecordMessageFailed(string messageType, string errorType);
    public static void RecordCircuitBreakerStateChange(string serviceName, CircuitState newState);
    public static void RecordDeadLetterMessage(string messageType, string reason);
}
```

## Serialization Support

### Polymorphic JSON Converter
- **`PolymorphicJsonConverter`** - Handles inheritance hierarchies
- **Type Discrimination** - Automatic type resolution
- **Performance Optimized** - Minimal reflection overhead

## Configuration Patterns

### Bus Configuration Fluent API
```csharp
services.UseSourceFlowAws(
    options => { options.Region = RegionEndpoint.USEast1; },
    bus => bus
        .Send
            .Command<CreateOrderCommand>(q => q.Queue("orders.fifo"))
            .Command<UpdateOrderCommand>(q => q.Queue("orders.fifo"))
        .Raise
            .Event<OrderCreatedEvent>(t => t.Topic("order-events"))
            .Event<OrderUpdatedEvent>(t => t.Topic("order-events"))
        .Listen.To
            .CommandQueue("orders.fifo")
            .CommandQueue("inventory.fifo")
        .Subscribe.To
            .Topic("order-events")
            .Topic("payment-events"));
```

### Configuration Features
- **Short Names** - Provide only queue/topic names, not full URLs/ARNs
- **Automatic Resolution** - Bootstrapper resolves full paths at startup
- **Resource Creation** - Missing queues/topics created automatically
- **Type Safety** - Compile-time validation of command/event routing
- **Fluent Chaining** - Natural, readable configuration syntax

### Idempotency Service
- **`IIdempotencyService`** - Duplicate message detection interface
- **`InMemoryIdempotencyService`** - Default in-memory implementation
- **`IdempotencyConfigurationBuilder`** - Fluent API for configuring idempotency services
- **Configurable TTL** - Automatic cleanup of old entries
- **Multi-Instance Support** - SQL-based implementation available via Entity Framework package

### Idempotency Configuration

SourceFlow provides multiple ways to configure idempotency services:

#### Direct Service Registration
```csharp
// In-memory (default for single instance)
services.AddScoped<IIdempotencyService, InMemoryIdempotencyService>();

// SQL-based (for multi-instance)
services.AddSourceFlowIdempotency(connectionString, cleanupIntervalMinutes: 60);

// Custom implementation
services.AddScoped<IIdempotencyService, MyCustomIdempotencyService>();
```

#### Fluent Builder API
```csharp
// Entity Framework-based (multi-instance)
// Note: Requires SourceFlow.Stores.EntityFramework package
// Uses reflection to avoid direct dependency in core package
var idempotencyBuilder = new IdempotencyConfigurationBuilder()
    .UseEFIdempotency(connectionString, cleanupIntervalMinutes: 60);

// In-memory (single-instance)
var idempotencyBuilder = new IdempotencyConfigurationBuilder()
    .UseInMemory();

// Custom implementation with type
var idempotencyBuilder = new IdempotencyConfigurationBuilder()
    .UseCustom<MyCustomIdempotencyService>();

// Custom implementation with factory
var idempotencyBuilder = new IdempotencyConfigurationBuilder()
    .UseCustom(provider => new MyCustomIdempotencyService(
        provider.GetRequiredService<ILogger<MyCustomIdempotencyService>>()));

// Apply configuration (uses TryAddScoped for default registration)
idempotencyBuilder.Build(services);
```

#### Cloud Provider Integration
```csharp
// AWS with explicit idempotency configuration
services.UseSourceFlowAws(
    options => { options.Region = RegionEndpoint.USEast1; },
    bus => bus.Send.Command<CreateOrderCommand>(q => q.Queue("orders.fifo")),
    configureIdempotency: services =>
    {
        services.AddSourceFlowIdempotency(connectionString);
    });

// Or pre-register before cloud configuration
services.AddSourceFlowIdempotency(connectionString);
services.UseSourceFlowAws(
    options => { options.Region = RegionEndpoint.USEast1; },
    bus => bus.Send.Command<CreateOrderCommand>(q => q.Queue("orders.fifo")));
```

**Builder Methods:**
- `UseEFIdempotency(connectionString, cleanupIntervalMinutes)` - Entity Framework-based (requires SourceFlow.Stores.EntityFramework package)
- `UseInMemory()` - In-memory implementation (default)
- `UseCustom<TImplementation>()` - Custom implementation by type
- `UseCustom(factory)` - Custom implementation with factory function
- `Build(services)` - Apply configuration to service collection

**See Also**: [Idempotency Configuration Guide](../../docs/Idempotency-Configuration-Guide.md)

## Development Guidelines

### Bus Configuration Best Practices
- Use short names only (e.g., "orders.fifo", not full URLs)
- Group related commands to the same queue for ordering
- Use FIFO queues (.fifo suffix) when order matters
- Configure listening queues before subscribing to topics
- Let the bootstrapper handle resource creation in development
- Use infrastructure-as-code for production deployments

### Circuit Breaker Usage
- Use for external service calls
- Configure appropriate thresholds per service
- Monitor state changes and failure patterns
- Implement fallback strategies for open circuits
- Handle `CircuitBreakerOpenException` gracefully

### Security Implementation
- Always encrypt sensitive data in messages
- Use `[SensitiveData]` attribute for automatic handling
- Implement proper key rotation strategies
- Audit encryption/decryption operations

### Dead Letter Handling
- Implement custom processors for business-specific logic
- Monitor dead letter queues for operational issues
- Implement retry strategies with exponential backoff
- Archive messages that cannot be processed

### Observability Best Practices
- Use structured logging with correlation IDs
- Implement custom metrics for business operations
- Create dashboards for operational monitoring
- Set up alerts for critical failure patterns

### Multi-Region Considerations
- Design for eventual consistency
- Implement proper failover strategies
- Consider data sovereignty requirements
- Plan for cross-region communication patterns