# SourceFlow.Net - Architecture Overview

## Introduction
SourceFlow.Net is an event-driven architecture framework implementing Command Query Responsibility Segregation (CQRS) and Event Sourcing patterns. The system separates command processing from event handling, enabling scalable and maintainable domain-driven design.

## Core Architectural Patterns

### 1. CQRS (Command Query Responsibility Segregation)
- **Commands**: Modify state through CommandBus
- **Queries**: Read from materialized views (ViewModels)
- Clear separation between write and read models

### 2. Event Sourcing
- Commands are persisted in CommandStore
- Events represent state changes
- Event replay capability for reconstructing state

### 3. Saga Pattern
- Long-running business processes
- Coordinate multiple commands across aggregates
- Handle complex workflows

## High-Level Component Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                      Client Application                      │
└───────────────────┬─────────────────────────────┬────────────┘
                    │                             │
                    ▼                             ▼
         ┌──────────────────┐          ┌──────────────────┐
         │   ICommandBus    │          │  IEventQueue     │
         │   (Publish)      │          │  (Enqueue)       │
         └────────┬─────────┘          └────────┬─────────┘
                  │                             │
                  ▼                             ▼
      ┌──────────────────────┐      ┌──────────────────────┐
      │ ICommandDispatcher[] │      │ IEventDispatcher[]   │
      └──────────┬───────────┘      └──────────┬───────────┘
                 │                             │
                 ▼                             │
      ┌──────────────────────┐                │
      │ ICommandSubscriber[] │                │
      │   - CommandSubscriber│                │
      │     (routes to Sagas)│                │
      └──────────┬───────────┘                │
                 │                             │
                 ▼                             ▼
         ┌───────────────┐         ┌─────────────────────┐
         │   ISaga[]     │         │ IEventSubscriber[]  │
         │   (Handles    │         │ - Aggregate.        │
         │    Commands)  │         │   EventSubscriber   │
         └───────┬───────┘         │ - Projections.      │
                 │                 │   EventSubscriber   │
                 │ Publishes       └──────────┬──────────┘
                 │ Events                     │
                 │                            │
                 ▼                            ▼
         ┌───────────────┐         ┌─────────────────────┐
         │  IEventQueue  │         │  IAggregate[]       │
         │               │         │  IView[]            │
         └───────────────┘         │  (Subscribe/Project)│
                                   └─────────────────────┘
```

## Key Components

### Command Processing Path
1. **ICommandBus** - Entry point for command publishing
2. **CommandBus** - Manages command persistence and dispatching
3. **ICommandDispatcher** - Routes commands to subscribers
4. **CommandDispatcher** - Dispatches to all registered ICommandSubscriber instances
5. **ICommandSubscriber** - Receives dispatched commands
6. **CommandSubscriber** - Routes commands to appropriate Sagas
7. **ISaga** - Handles commands and produces events

### Event Processing Path
1. **IEventQueue** - Entry point for event publishing
2. **EventQueue** - Manages event distribution
3. **IEventDispatcher** - Routes events to subscribers
4. **EventDispatcher** - Dispatches to all registered IEventSubscriber instances
5. **IEventSubscriber** - Receives dispatched events
   - **Aggregate.EventSubscriber** - Routes to Aggregates implementing ISubscribes<TEvent>
   - **Projections.EventSubscriber** - Routes to Views implementing IProjectOn<TEvent>

## Storage Abstractions

### Command Storage
- **ICommandStore** - Interface for command persistence
- **ICommandStoreAdapter** - Scoped adapter wrapping ICommandStore
- Stores commands with sequence numbers for replay

### Entity Storage (Aggregates)
- **IEntityStore** - Interface for entity persistence
- **IEntityStoreAdapter** - Scoped adapter wrapping IEntityStore
- Stores aggregate state

### ViewModel Storage (Projections)
- **IViewModelStore** - Interface for read model persistence
- **IViewModelStoreAdapter** - Scoped adapter wrapping IViewModelStore
- Stores materialized views for queries

## Service Lifetimes

### Singleton Services
- **IEventQueue** - Thread-safe event distribution
- **IEventDispatcher** - Stateless event routing
- **IEventSubscriber** (both implementations) - Stateless subscription management
- **IDomainTelemetryService** - Observability and tracing

### Scoped Services
- **ICommandBus** - Per-request command handling
- **ICommandDispatcher** - Per-request command routing
- **ICommandSubscriber** - Per-request subscription handling
- **ICommandPublisher** - Per-request command publishing
- **Store Adapters** (ICommandStoreAdapter, IEntityStoreAdapter, IViewModelStoreAdapter)

### Configurable Lifetime (Default: Singleton)
- **ISaga** implementations
- **IAggregate** implementations
- **IView** implementations

## Dependency Injection Registration

Components are registered using the `UseSourceFlow()` extension method:

```csharp
services.UseSourceFlow(ServiceLifetime.Singleton, assemblies);
```

Key registration points (from IocExtensions.cs:33-98):
- Stores and adapters auto-discovered from assemblies
- Factories registered for aggregate creation
- Lazy<ICommandPublisher> to break circular dependencies
- Event/Command subscribers registered as Singleton/Scoped respectively

## Observability and Telemetry

The framework includes built-in OpenTelemetry support:
- **IDomainTelemetryService** - Provides distributed tracing
- Traces command dispatching, event publishing, and replay operations
- Tags include: command/event type, entity IDs, sequence numbers, subscriber counts

Trace operations:
- `sourceflow.commandbus.dispatch`
- `sourceflow.commandbus.replay`
- `sourceflow.commanddispatcher.send`
- `sourceflow.eventqueue.enqueue`
- `sourceflow.eventdispatcher.dispatch`

## Performance Optimizations

### TaskBufferPool
- ArrayPool-based task collection optimization
- Used in CommandDispatcher and EventDispatcher
- Reduces allocations for parallel subscriber execution

## Key Design Principles

1. **Separation of Concerns**: Commands, Events, Aggregates, Sagas, and Views are distinct
2. **Interface-based Design**: All major components use interfaces for extensibility
3. **Dependency Inversion**: Components depend on abstractions, not implementations
4. **Single Responsibility**: Each component has a focused purpose
5. **Open/Closed Principle**: Extensible through new implementations without modifying core

## Message Metadata

All commands and events implement IMetadata:
- **SequenceNo**: Order of command/event
- **IsReplay**: Flag indicating replay vs. new command/event
- Used for event sourcing and replay scenarios

## Next Steps for Cloud Extension

The architecture's interface-based design makes it suitable for cloud extension:
- New ICommandDispatcher implementation for AWS SQS
- New IEventDispatcher implementation for AWS SNS
- Selective routing based on command/event type
- Maintain existing local processing alongside cloud dispatch
