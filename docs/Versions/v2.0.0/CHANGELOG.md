# SourceFlow.Net v2.0.0 - Changelog

**Release Date**: TBC  
**Status**: In Development

## 🎉 Major Changes

### Cloud Core Consolidation

The `SourceFlow.Cloud.Core` project has been **consolidated into the main SourceFlow package**. This architectural change simplifies the dependency structure and reduces the number of separate packages required for cloud integration.

**Benefits:**
- ✅ Simplified package management (one less NuGet package)
- ✅ Reduced build complexity
- ✅ Improved discoverability (cloud functionality is part of core)
- ✅ Better performance (eliminates one layer of assembly loading)
- ✅ Easier testing (no intermediate package dependencies)

## 🔄 Breaking Changes

### Namespace Changes

All cloud core functionality has been moved from `SourceFlow.Cloud.Core.*` to `SourceFlow.Cloud.*`:

| Old Namespace | New Namespace |
|--------------|---------------|
| `SourceFlow.Cloud.Core.Configuration` | `SourceFlow.Cloud.Configuration` |
| `SourceFlow.Cloud.Core.Resilience` | `SourceFlow.Cloud.Resilience` |
| `SourceFlow.Cloud.Core.Security` | `SourceFlow.Cloud.Security` |
| `SourceFlow.Cloud.Core.Observability` | `SourceFlow.Cloud.Observability` |
| `SourceFlow.Cloud.Core.DeadLetter` | `SourceFlow.Cloud.DeadLetter` |
| `SourceFlow.Cloud.Core.Serialization` | `SourceFlow.Cloud.Serialization` |

### Migration Guide

**Step 1: Update Package References**

Remove the `SourceFlow.Cloud.Core` package reference (if you were using it directly):

```xml
<!-- Remove this -->
<PackageReference Include="SourceFlow.Cloud.Core" Version="1.0.0" />
```

**Step 2: Update Using Statements**

Update your using statements:

```csharp
// Before (v1.0.0)
using SourceFlow.Cloud.Core.Configuration;
using SourceFlow.Cloud.Core.Resilience;
using SourceFlow.Cloud.Core.Security;

// After (v2.0.0)
using SourceFlow.Cloud.Configuration;
using SourceFlow.Cloud.Resilience;
using SourceFlow.Cloud.Security;
```

**Step 3: Update Project References**

Cloud extension projects now reference only the core `SourceFlow` project:

```xml
<!-- Before (v1.0.0) -->
<ItemGroup>
  <ProjectReference Include="..\SourceFlow\SourceFlow.csproj" />
  <ProjectReference Include="..\SourceFlow.Cloud.Core\SourceFlow.Cloud.Core.csproj" />
</ItemGroup>

<!-- After (v2.0.0) -->
<ItemGroup>
  <ProjectReference Include="..\SourceFlow\SourceFlow.csproj" />
</ItemGroup>
```

## ✨ New Features

### Integrated Cloud Functionality

The following components are now part of the core `SourceFlow` package:

#### Configuration
- `BusConfiguration` - Fluent API for routing configuration
- `IBusBootstrapConfiguration` - Bootstrapper integration
- `ICommandRoutingConfiguration` - Command routing abstraction
- `IEventRoutingConfiguration` - Event routing abstraction
- `IIdempotencyService` - Duplicate message detection
- `InMemoryIdempotencyService` - Default implementation
- `IdempotencyConfigurationBuilder` - Fluent API for idempotency configuration

#### Resilience
- `ICircuitBreaker` - Circuit breaker pattern interface
- `CircuitBreaker` - Implementation with state management
- `CircuitBreakerOptions` - Configuration options
- `CircuitBreakerOpenException` - Exception for open circuits
- `CircuitBreakerStateChangedEventArgs` - State transition events

#### Security
- `IMessageEncryption` - Message encryption abstraction
- `SensitiveDataAttribute` - Marks properties for encryption
- `SensitiveDataMasker` - Automatic log masking
- `EncryptionOptions` - Encryption configuration

#### Dead Letter Processing
- `IDeadLetterProcessor` - Failed message handling
- `IDeadLetterStore` - Failed message persistence
- `DeadLetterRecord` - Failed message model
- `InMemoryDeadLetterStore` - Default implementation

#### Observability
- `CloudActivitySource` - OpenTelemetry activity source
- `CloudMetrics` - Standard cloud metrics
- `CloudTelemetry` - Centralized telemetry

#### Serialization
- `PolymorphicJsonConverter` - Handles inheritance hierarchies

### Idempotency Configuration Builder

New fluent API for configuring idempotency services:

```csharp
// Entity Framework-based (multi-instance)
var idempotencyBuilder = new IdempotencyConfigurationBuilder()
    .UseEFIdempotency(connectionString, cleanupIntervalMinutes: 60);

// In-memory (single-instance)
var idempotencyBuilder = new IdempotencyConfigurationBuilder()
    .UseInMemory();

// Custom implementation
var idempotencyBuilder = new IdempotencyConfigurationBuilder()
    .UseCustom<MyCustomIdempotencyService>();

// Apply configuration
idempotencyBuilder.Build(services);
```

**Builder Methods:**
- `UseEFIdempotency(connectionString, cleanupIntervalMinutes)` - Entity Framework-based (requires SourceFlow.Stores.EntityFramework package)
- `UseInMemory()` - In-memory implementation
- `UseCustom<TImplementation>()` - Custom implementation by type
- `UseCustom(factory)` - Custom implementation with factory function

### Enhanced AWS Integration

AWS cloud extension now supports explicit idempotency configuration:

```csharp
services.UseSourceFlowAws(
    options => { options.Region = RegionEndpoint.USEast1; },
    bus => bus.Send.Command<CreateOrderCommand>(q => q.Queue("orders.fifo")),
    configureIdempotency: services =>
    {
        services.AddSourceFlowIdempotency(connectionString);
    });
```

## 📚 Documentation Updates

### New Documentation
- [Cloud Core Consolidation Guide](../Architecture/06-Cloud-Core-Consolidation.md) - Complete migration guide
- [Idempotency Configuration Guide](../Idempotency-Configuration-Guide.md) - Comprehensive idempotency setup guide
- [SQL-Based Idempotency Service](../SQL-Based-Idempotency-Service.md) - Multi-instance idempotency details

### Updated Documentation
- [SourceFlow Core](../SourceFlow.Net-README.md) - Updated with cloud functionality
- [AWS Cloud Extension](.kiro/steering/sourceflow-cloud-aws.md) - Updated with idempotency configuration
- [Azure Cloud Extension](.kiro/steering/sourceflow-cloud-azure.md) - Updated architecture references

## 🐛 Bug Fixes

- None (this is a major architectural release)

## 🔧 Internal Changes

### Project Structure
- Consolidated `src/SourceFlow.Cloud.Core/` into `src/SourceFlow/Cloud/`
- Simplified dependency graph for cloud extensions
- Reduced NuGet package count

### Build System
- Updated project references to remove Cloud.Core dependency
- Simplified build pipeline
- Reduced compilation time

## 📦 Package Dependencies

### SourceFlow v2.0.0
- No new dependencies added
- Cloud functionality now integrated

### SourceFlow.Cloud.AWS v2.0.0
- Depends on: `SourceFlow >= 2.0.0`
- Removed: `SourceFlow.Cloud.Core` dependency

### SourceFlow.Cloud.Azure v2.0.0
- Depends on: `SourceFlow >= 2.0.0`
- Removed: `SourceFlow.Cloud.Core` dependency

## 🚀 Upgrade Path

### For End Users (AWS/Azure Extensions)

If you're using the AWS or Azure cloud extensions, **no code changes are required**. The consolidation is transparent to consumers of the cloud packages.

### For Direct Cloud.Core Users

If you were directly referencing `SourceFlow.Cloud.Core` (not recommended):

1. Remove the `SourceFlow.Cloud.Core` package reference
2. Add a reference to `SourceFlow` instead (if not already present)
3. Update namespace imports as shown in the Migration Guide above

## 📝 Notes

- This is a **major version** release due to breaking namespace changes
- The consolidation improves the overall architecture and developer experience
- All functionality from Cloud.Core is preserved in the main SourceFlow package
- Cloud extensions (AWS, Azure) remain separate packages with simplified dependencies

## 🔗 Related Documentation

- [Architecture Overview](../Architecture/01-Architecture-Overview.md)
- [Cloud Configuration Guide](../SourceFlow.Net-README.md#-cloud-configuration-with-bus-configuration-system)
- [AWS Cloud Extension](.kiro/steering/sourceflow-cloud-aws.md)
- [Azure Cloud Extension](.kiro/steering/sourceflow-cloud-azure.md)

---

**Version**: 2.0.0  
**Date**: TBC  
**Status**: In Development
