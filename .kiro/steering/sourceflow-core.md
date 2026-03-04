# SourceFlow Core Framework

**Project**: `src/SourceFlow/`  
**Purpose**: Main framework library implementing CQRS, Event Sourcing, and Saga patterns

## Core Architecture

### Key Components
- **Commands & Events** - Message-based communication primitives
- **Sagas** - Long-running transaction orchestrators that handle commands
- **Aggregates** - Domain entities that subscribe to events and maintain state
- **Projections/Views** - Read model generators that project events to view models
- **Command Bus** - Orchestrates command processing with sequence numbering
- **Event Queue** - Manages event distribution to subscribers

### Processing Flow
```
Command → CommandBus → CommandDispatcher → CommandSubscriber → Saga → Events
Event → EventQueue → EventDispatcher → EventSubscriber → Aggregate/View
```

## Key Interfaces

### Command Processing
- `ICommand` - Command message contract with Entity reference and Payload
- `ISaga` - Command handlers that orchestrate business workflows
- `ICommandBus` - Entry point for publishing commands and replay
- `ICommandDispatcher` - Routes commands to subscribers (extensible)

### Event Processing  
- `IEvent` - Event message contract
- `IAggregate` - Domain entities that subscribe to events (`ISubscribes<TEvent>`)
- `IView` - Read model projections (`IProjectOn<TEvent>`)
- `IEventQueue` - Entry point for publishing events

### Storage Abstractions
- `ICommandStore` - Event sourcing log (append-only, sequenced)
- `IEntityStore` - Saga/aggregate state persistence (mutable)
- `IViewModelStore` - Read model persistence (denormalized)

## Service Registration

### Core Pattern
```csharp
services.UseSourceFlow(ServiceLifetime.Singleton, assemblies);
```

### Service Lifetimes
- **Scoped**: Command pipeline, store adapters (transaction boundaries)
- **Singleton**: Event pipeline, domain components, telemetry (stateless)
- **Configurable**: Sagas, Aggregates, Views (default: Singleton)

## Extension Points

### Dispatcher Collections
- Multiple `ICommandDispatcher` instances for local + cloud routing
- Multiple `IEventDispatcher` instances for fan-out scenarios
- Plugin architecture - add dispatchers without modifying core

### Store Implementations
- Implement `ICommandStore`, `IEntityStore`, `IViewModelStore`
- Automatic adapter wrapping for telemetry and serialization

## Key Patterns

### Type Safety
- Generic types preserved throughout pipeline
- No reflection except during replay
- Compile-time command/event routing

### Performance Optimizations
- `TaskBufferPool` - ArrayPool for task collections
- `ByteArrayPool` - Pooled serialization buffers
- Parallel dispatcher execution

### Observability
- Built-in OpenTelemetry integration
- `IDomainTelemetryService` for metrics and tracing
- Configurable via `DomainObservabilityOptions`

## Folder Structure
- `Messaging/` - Commands, events, bus implementations
- `Saga/` - Command handling and orchestration
- `Aggregate/` - Event subscription and domain state
- `Projections/` - View model generation
- `Observability/` - Telemetry and tracing
- `Performance/` - Memory optimization utilities
- `Cloud/` - Cloud integration infrastructure
  - `Configuration/` - Bus configuration and routing
  - `Resilience/` - Circuit breaker patterns
  - `Security/` - Encryption and data masking
  - `Observability/` - Cloud telemetry
  - `DeadLetter/` - Failed message handling
  - `Serialization/` - Polymorphic JSON converters

## Development Guidelines
- Implement `IHandles<TCommand>` for saga command handlers
- Implement `ISubscribes<TEvent>` for aggregate event handlers  
- Implement `IProjectOn<TEvent>` for view projections
- Use `EntityRef` for command entity references
- Commands are immutable after creation
- Events represent facts that have occurred