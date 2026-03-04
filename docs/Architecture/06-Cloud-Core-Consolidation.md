# Cloud Core Consolidation

## Overview

As of the latest architecture update, the `SourceFlow.Cloud.Core` project has been consolidated into the main `SourceFlow` project. This architectural change simplifies the dependency structure and reduces the number of separate packages required for cloud integration.

## Motivation

The consolidation was driven by several factors:

1. **Simplified Dependencies** - Eliminates an intermediate package layer
2. **Reduced Complexity** - Fewer projects to maintain and version
3. **Better Developer Experience** - Single core package contains all fundamental functionality
4. **Cleaner Architecture** - Cloud abstractions are part of the core framework

## Changes

### Project Structure

**Before:**
```
src/
├── SourceFlow/                    # Core framework
├── SourceFlow.Cloud.Core/         # Shared cloud functionality
├── SourceFlow.Cloud.AWS/          # AWS integration (depends on Cloud.Core)
└── SourceFlow.Cloud.Azure/        # Azure integration (depends on Cloud.Core)
```

**After:**
```
src/
├── SourceFlow/                    # Core framework with integrated cloud functionality
│   └── Cloud/                     # Cloud abstractions and patterns
│       ├── Configuration/         # Bus configuration and routing
│       ├── Resilience/           # Circuit breaker patterns
│       ├── Security/             # Encryption and data masking
│       ├── Observability/        # Cloud telemetry
│       ├── DeadLetter/           # Failed message handling
│       └── Serialization/        # Polymorphic JSON converters
├── SourceFlow.Cloud.AWS/          # AWS integration (depends only on SourceFlow)
└── SourceFlow.Cloud.Azure/        # Azure integration (depends only on SourceFlow)
```

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

For existing code using the old namespaces, update your using statements:

**Before:**
```csharp
using SourceFlow.Cloud.Core.Configuration;
using SourceFlow.Cloud.Core.Resilience;
using SourceFlow.Cloud.Core.Security;
```

**After:**
```csharp
using SourceFlow.Cloud.Configuration;
using SourceFlow.Cloud.Resilience;
using SourceFlow.Cloud.Security;
```

### Project References

Cloud extension projects now reference only the core `SourceFlow` project:

**Before (SourceFlow.Cloud.AWS.csproj):**
```xml
<ItemGroup>
  <ProjectReference Include="..\SourceFlow\SourceFlow.csproj" />
  <ProjectReference Include="..\SourceFlow.Cloud.Core\SourceFlow.Cloud.Core.csproj" />
</ItemGroup>
```

**After (SourceFlow.Cloud.AWS.csproj):**
```xml
<ItemGroup>
  <ProjectReference Include="..\SourceFlow\SourceFlow.csproj" />
</ItemGroup>
```

## Benefits

1. **Simplified Package Management** - One less NuGet package to manage and version
2. **Reduced Build Complexity** - Fewer project dependencies to track
3. **Improved Discoverability** - Cloud functionality is part of the core framework
4. **Easier Testing** - No need to mock intermediate package dependencies
5. **Better Performance** - Eliminates one layer of assembly loading

## Components Consolidated

The following components are now part of the core `SourceFlow` package:

### Configuration
- `BusConfiguration` - Fluent API for routing configuration
- `IBusBootstrapConfiguration` - Bootstrapper integration
- `ICommandRoutingConfiguration` - Command routing abstraction
- `IEventRoutingConfiguration` - Event routing abstraction
- `IIdempotencyService` - Duplicate message detection
- `InMemoryIdempotencyService` - Default implementation

### Resilience
- `ICircuitBreaker` - Circuit breaker pattern interface
- `CircuitBreaker` - Implementation with state management
- `CircuitBreakerOptions` - Configuration options
- `CircuitBreakerOpenException` - Exception for open circuits
- `CircuitBreakerStateChangedEventArgs` - State transition events

### Security
- `IMessageEncryption` - Message encryption abstraction
- `SensitiveDataAttribute` - Marks properties for encryption
- `SensitiveDataMasker` - Automatic log masking
- `EncryptionOptions` - Encryption configuration

### Dead Letter Processing
- `IDeadLetterProcessor` - Failed message handling
- `IDeadLetterStore` - Failed message persistence
- `DeadLetterRecord` - Failed message model
- `InMemoryDeadLetterStore` - Default implementation

### Observability
- `CloudActivitySource` - OpenTelemetry activity source
- `CloudMetrics` - Standard cloud metrics
- `CloudTelemetry` - Centralized telemetry

### Serialization
- `PolymorphicJsonConverter` - Handles inheritance hierarchies

## Impact on Existing Code

### No Breaking Changes for End Users

If you're using the AWS or Azure cloud extensions, no code changes are required. The consolidation is transparent to consumers of the cloud packages.

### Breaking Changes for Direct Cloud.Core Users

If you were directly referencing `SourceFlow.Cloud.Core` (not recommended), you'll need to:

1. Remove the `SourceFlow.Cloud.Core` package reference
2. Add a reference to `SourceFlow` instead
3. Update namespace imports as shown in the Migration Guide above

## Future Considerations

This consolidation sets the stage for:

1. **Unified Cloud Abstractions** - Common patterns across all cloud providers
2. **Extensibility** - Easier to add new cloud providers
3. **Hybrid Cloud Support** - Simplified multi-cloud scenarios
4. **Local Development** - Cloud patterns available without cloud dependencies

## Related Documentation

- [SourceFlow Core](./01-Architecture-Overview.md)
- [Cloud Configuration Guide](../SourceFlow.Net-README.md#-cloud-configuration-with-bus-configuration-system)
- [AWS Cloud Extension](../../.kiro/steering/sourceflow-cloud-aws.md)
- [Azure Cloud Extension](../../.kiro/steering/sourceflow-cloud-azure.md)

---

**Date**: March 3, 2026  
**Version**: 2.0.0  
**Status**: Implemented
