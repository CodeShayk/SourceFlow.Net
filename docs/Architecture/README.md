# SourceFlow.Net Core Functionality Summary

## Overview

This document provides a comprehensive summary of the core SourceFlow.Net functionality, distilled from the detailed analysis documents (01-05). It covers the essential architecture, patterns, and components that make up the framework.

**Target Audience**: Developers who want to understand SourceFlow.Net's core concepts quickly before diving into detailed documentation or cloud extensions.

---

## Table of Contents

1. [What is SourceFlow.Net?](#what-is-sourceflownet)
2. [Core Architectural Patterns](#core-architectural-patterns)
3. [Key Components](#key-components)
4. [Command Processing Flow](#command-processing-flow)
5. [Event Processing Flow](#event-processing-flow)
6. [Dispatcher Pattern Architecture](#dispatcher-pattern-architecture)
7. [Storage and Persistence](#storage-and-persistence)
8. [Service Lifetimes](#service-lifetimes)
9. [Extension Points](#extension-points)
10. [Key Design Principles](#key-design-principles)

---

## What is SourceFlow.Net?

**SourceFlow.Net** is an event-driven architecture framework for .NET that implements:
- **CQRS** (Command Query Responsibility Segregation)
- **Event Sourcing**
- **Saga Pattern** for orchestrating complex workflows
- **Domain-Driven Design** principles

### Primary Use Cases
- Building scalable, event-driven applications
- Implementing complex business workflows across aggregates
- Separating read and write concerns
- Maintaining audit trails through event sourcing
- Enabling cloud-native architectures

### Key Characteristics
- **Zero modification extensibility**: Add cloud dispatchers without changing core code
- **Type-safe**: Generic types preserved throughout the pipeline
- **Observable**: Built-in OpenTelemetry support
- **Performant**: Optimized with ArrayPools and parallel processing

---

## Core Architectural Patterns

### 1. CQRS (Command Query Responsibility Segregation)

**Write Side** (Commands):
```
Command → CommandBus → Saga → Events → CommandStore
```

**Read Side** (Queries):
```
Event → EventQueue → View → ViewModel → ViewModelStore
```

**Benefits**:
- Optimized write models for business logic
- Optimized read models for queries
- Independent scaling of reads and writes

---

### 2. Event Sourcing

**Concept**: Store all changes as a sequence of commands (events)

**Implementation**:
```csharp
// Commands are persisted with sequence numbers
CommandStore.Append(command); // Sequence: 1, 2, 3...

// State can be reconstructed by replaying commands
CommandBus.Replay(entityId); // Replays all commands in order
```

**Benefits**:
- Complete audit trail
- State reconstruction from history
- Time-travel debugging
- Compliance and regulatory support

---

### 3. Saga Pattern

**Purpose**: Orchestrate long-running business processes across multiple aggregates

**Example**:
```csharp
public class OrderSaga : Saga<OrderEntity>, IHandles<CreateOrderCommand>
{
    public async Task<IEntity> Handle(IEntity entity, CreateOrderCommand command)
    {
        // 1. Update order state
        var order = entity as OrderEntity;
        order.Status = OrderStatus.Created;

        // 2. Publish events
        await PublishEvent(new OrderCreatedEvent { OrderId = order.Id });

        // 3. Trigger next command
        await PublishCommand(new ProcessPaymentCommand { OrderId = order.Id });

        return order;
    }
}
```

**Benefits**:
- Coordinates complex workflows
- Maintains consistency across aggregates
- Handles failures gracefully

---

## Key Components

### Component Hierarchy

```
┌─────────────────────────────────────────────────────────┐
│                  Client Application                      │
└───────────┬─────────────────────────────┬───────────────┘
            │                             │
            ▼                             ▼
    ┌──────────────┐              ┌──────────────┐
    │ ICommandBus  │              │ IEventQueue  │
    │              │              │              │
    │ - Publish    │              │ - Enqueue    │
    │ - Replay     │              │              │
    └──────┬───────┘              └──────┬───────┘
           │                             │
           │                             │
    ┌──────▼────────────┐        ┌──────▼────────────┐
    │ CommandDispatcher │        │ EventDispatcher   │
    │ (routes to        │        │ (routes to        │
    │  subscribers)     │        │  subscribers)     │
    └──────┬────────────┘        └──────┬────────────┘
           │                             │
           │                             │
    ┌──────▼─────────────┐      ┌───────▼──────────────┐
    │ CommandSubscriber  │      │ EventSubscriber      │
    │ (routes to sagas)  │      │ - Aggregate          │
    └──────┬─────────────┘      │ - Projections        │
           │                    └───────┬──────────────┘
           │                            │
           ▼                            ▼
    ┌─────────────┐            ┌────────────────────┐
    │   ISaga[]   │            │  IAggregate[]      │
    │             │            │  IView[]           │
    │ - Handles   │            │                    │
    │   commands  │            │  - Subscribe to    │
    │ - Publishes │            │    events          │
    │   events    │            │  - Project views   │
    └─────────────┘            └────────────────────┘
```

---

### Core Interfaces

#### Commands
```csharp
public interface ICommandBus
{
    Task Publish<TCommand>(TCommand command) where TCommand : ICommand;
    Task Replay(int entityId);
}

public interface ICommandDispatcher
{
    Task Dispatch<TCommand>(TCommand command) where TCommand : ICommand;
}

public interface ICommandSubscriber
{
    Task Subscribe<TCommand>(TCommand command) where TCommand : ICommand;
}

public interface ISaga
{
    Task Handle<TCommand>(TCommand command) where TCommand : ICommand;
}

public interface IHandles<in TCommand> where TCommand : ICommand
{
    Task<IEntity> Handle(IEntity entity, TCommand command);
}
```

#### Events
```csharp
public interface IEventQueue
{
    Task Enqueue<TEvent>(TEvent @event) where TEvent : IEvent;
}

public interface IEventDispatcher
{
    Task Dispatch<TEvent>(TEvent @event) where TEvent : IEvent;
}

public interface IEventSubscriber
{
    Task Subscribe<TEvent>(TEvent @event) where TEvent : IEvent;
}

public interface ISubscribes<in TEvent> where TEvent : IEvent
{
    Task On(TEvent @event); // For Aggregates
}

public interface IProjectOn<TEvent> where TEvent : IEvent
{
    Task<IViewModel> On(TEvent @event); // For Views
}
```

---

## Command Processing Flow

### High-Level Flow

```
1. Client publishes command
   ↓
2. CommandBus assigns sequence number
   ↓
3. CommandBus dispatches to ALL ICommandDispatcher instances (parallel)
   ↓
4. CommandDispatcher routes to ALL ICommandSubscriber instances (parallel)
   ↓
5. CommandSubscriber filters and routes to matching Sagas
   ↓
6. Saga handles command:
   - Loads entity state
   - Executes business logic
   - Publishes events
   - Saves entity state
   ↓
7. CommandBus persists command to CommandStore (if not replay)
```

### Detailed Example

```csharp
// 1. Client publishes command
await commandBus.Publish(new CreateOrderCommand
{
    Entity = new EntityRef { Id = 123 },
    Payload = new CreateOrderCommandData { CustomerId = 456 }
});

// 2. CommandBus processes
// - Assigns SequenceNo (e.g., 1)
// - Dispatches to all ICommandDispatcher instances
// - Persists to CommandStore

// 3. CommandDispatcher routes to CommandSubscriber

// 4. CommandSubscriber checks which Sagas can handle CreateOrderCommand
// - OrderSaga implements IHandles<CreateOrderCommand> ✓
// - PaymentSaga doesn't implement it ✗

// 5. OrderSaga.Handle is invoked
public async Task<IEntity> Handle(IEntity entity, CreateOrderCommand command)
{
    var order = entity as OrderEntity ?? new OrderEntity { Id = command.Entity.Id };
    order.CustomerId = command.Payload.CustomerId;
    order.Status = OrderStatus.Pending;

    // Publish event
    await PublishEvent(new OrderCreatedEvent { OrderId = order.Id });

    return order;
}
```

### Key Points
- **Sequence numbers** ensure ordering per entity
- **Parallel dispatching** to multiple dispatchers/subscribers
- **Type-based routing** to appropriate sagas
- **Automatic persistence** for audit/replay

---

## Event Processing Flow

### High-Level Flow

```
1. Saga publishes event
   ↓
2. EventQueue enqueues event
   ↓
3. EventQueue dispatches to ALL IEventDispatcher instances (parallel)
   ↓
4. EventDispatcher routes to ALL IEventSubscriber instances (parallel)
   ↓
5. Aggregate EventSubscriber:
   - Routes to Aggregates implementing ISubscribes<TEvent>
   - Aggregate updates internal state (no persistence)
   ↓
6. Projections EventSubscriber:
   - Routes to Views implementing IProjectOn<TEvent>
   - View updates and persists read model
```

### Detailed Example

```csharp
// 1. Saga publishes event
await PublishEvent(new OrderCreatedEvent
{
    OrderId = 123,
    CustomerId = 456,
    Metadata = new Metadata { SequenceNo = 1 }
});

// 2. EventQueue processes
// - Dispatches to all IEventDispatcher instances

// 3. EventDispatcher routes to EventSubscriber instances
// - Aggregate.EventSubscriber
// - Projections.EventSubscriber

// 4a. Aggregate.EventSubscriber routes to Aggregates
public class OrderAggregate : IAggregate, ISubscribes<OrderCreatedEvent>
{
    public Task On(OrderCreatedEvent @event)
    {
        // Update in-memory state (event-sourced, no persistence)
        _state.OrderCount++;
        return Task.CompletedTask;
    }
}

// 4b. Projections.EventSubscriber routes to Views
public class OrderView : IView, IProjectOn<OrderCreatedEvent>
{
    public async Task<IViewModel> On(OrderCreatedEvent @event)
    {
        // Load or create view model
        var viewModel = await viewModelStore.Get<OrderViewModel>(@event.OrderId)
                     ?? new OrderViewModel { Id = @event.OrderId };

        // Update read model
        viewModel.CustomerId = @event.CustomerId;
        viewModel.Status = "Created";

        // Persist to store
        await viewModelStore.Persist(viewModel);

        return viewModel;
    }
}
```

### Key Points
- **Fan-out** to multiple subscribers
- **Aggregates** update state but don't persist (event-sourced)
- **Views** materialize and persist read models
- **Parallel processing** for performance

---

## Dispatcher Pattern Architecture

### Why Collections of Dispatchers?

**Core Design**:
```csharp
public class CommandBus
{
    private readonly IEnumerable<ICommandDispatcher> commandDispatchers;

    public async Task Publish<TCommand>(TCommand command)
    {
        // ALL dispatchers receive the command
        foreach (var dispatcher in commandDispatchers)
            tasks.Add(dispatcher.Dispatch(command));

        await Task.WhenAll(tasks);
    }
}
```

**Benefits**:
1. **Plugin Architecture**: Add new dispatchers without modifying CommandBus
2. **Multi-target**: Same command can go to local + AWS + Azure simultaneously
3. **Open/Closed Principle**: Open for extension, closed for modification

---

### Dispatcher → Subscriber → Handler Flow

```
ICommandDispatcher (routing layer)
    ↓
ICommandSubscriber (subscription layer)
    ↓
ISaga (handler layer)
```

**Responsibilities**:
- **Dispatcher**: "How to send" (local, SQS, Service Bus)
- **Subscriber**: "Who receives" (filter sagas by type)
- **Handler**: "What to do" (business logic)

---

### Current Implementations

**Commands**:
```csharp
// One CommandDispatcher (local)
services.AddScoped<ICommandDispatcher, CommandDispatcher>();

// One CommandSubscriber (routes to all sagas)
services.AddScoped<ICommandSubscriber, CommandSubscriber>();

// Multiple Sagas (user-defined)
services.AddScoped<ISaga, OrderSaga>();
services.AddScoped<ISaga, PaymentSaga>();
```

**Events**:
```csharp
// One EventDispatcher (local)
services.AddSingleton<IEventDispatcher, EventDispatcher>();

// Two EventSubscribers (aggregate + projections)
services.AddSingleton<IEventSubscriber, Aggregate.EventSubscriber>();
services.AddSingleton<IEventSubscriber, Projections.EventSubscriber>();

// Multiple Aggregates and Views (user-defined)
services.AddSingleton<IAggregate, OrderAggregate>();
services.AddSingleton<IView, OrderView>();
```

---

## Storage and Persistence

### Three Store Types

#### 1. ICommandStore - Event Sourcing Log

**Purpose**: Store all commands for replay and audit

**Characteristics**:
- Append-only (immutable)
- Sequenced per entity
- Serialized as CommandData

**Interface**:
```csharp
public interface ICommandStore
{
    Task Append(CommandData commandData);
    Task<IEnumerable<CommandData>> Load(int entityId);
}
```

**When Used**:
- After every command is processed
- During replay to reconstruct state

**Example**:
```csharp
// Append
await commandStore.Append(new CommandData
{
    EntityId = 123,
    SequenceNo = 1,
    CommandType = "MyApp.CreateOrderCommand, MyApp",
    PayloadData = "{\"CustomerId\": 456}"
});

// Load (for replay)
var commands = await commandStore.Load(123); // Returns all commands for entity 123
```

---

#### 2. IEntityStore - Saga/Aggregate State

**Purpose**: Store current state of saga entities

**Characteristics**:
- Mutable (CRUD operations)
- Transactional
- Domain objects (not serialized DTOs)

**Interface**:
```csharp
public interface IEntityStore
{
    Task<TEntity> Get<TEntity>(int id) where TEntity : class, IEntity;
    Task<TEntity> Persist<TEntity>(TEntity entity) where TEntity : class, IEntity;
    Task Delete<TEntity>(TEntity entity) where TEntity : class, IEntity;
}
```

**When Used**:
- Sagas load entity before handling command
- Sagas persist entity after handling command

**Example**:
```csharp
// In Saga
var order = await entityStore.Get<OrderEntity>(123);
order.Status = OrderStatus.Confirmed;
await entityStore.Persist(order);
```

---

#### 3. IViewModelStore - CQRS Read Models

**Purpose**: Store materialized views for queries

**Characteristics**:
- Denormalized (optimized for queries)
- Eventually consistent
- Updated by event projections

**Interface**:
```csharp
public interface IViewModelStore
{
    Task<TViewModel> Get<TViewModel>(int id) where TViewModel : class, IViewModel;
    Task<TViewModel> Persist<TViewModel>(TViewModel model) where TViewModel : class, IViewModel;
    Task Delete<TViewModel>(TViewModel model) where TViewModel : class, IViewModel;
}
```

**When Used**:
- Views project events to update read models
- Application queries read models

**Example**:
```csharp
// In View
var orderViewModel = await viewModelStore.Get<OrderViewModel>(123);
orderViewModel.CustomerName = "John Doe";
await viewModelStore.Persist(orderViewModel);

// In application (query)
var order = await viewModelStore.Get<OrderViewModel>(123);
```

---

### Store Adapter Pattern

**Purpose**: Add cross-cutting concerns to stores

```
Client → ICommandStoreAdapter → ICommandStore
            ↑
            |
            +-- Serialization
            +-- Telemetry
            +-- Sequence numbering
```

**Adapters**:
- `ICommandStoreAdapter`: Serialization, sequence number management
- `IEntityStoreAdapter`: Telemetry wrapping
- `IViewModelStoreAdapter`: Telemetry wrapping

**Why Separate?**:
- Stores focus on persistence
- Adapters add observability, serialization, lifecycle management
- Different lifetimes (adapters are Scoped, stores can be Singleton)

---

### CommandData DTO

**Purpose**: Serialization-friendly representation for persistence

```csharp
public class CommandData
{
    public int EntityId { get; set; }
    public int SequenceNo { get; set; }
    public string CommandName { get; set; }
    public string CommandType { get; set; }        // Full type name
    public string PayloadType { get; set; }        // Payload type name
    public string PayloadData { get; set; }        // JSON
    public string Metadata { get; set; }           // JSON
    public DateTime Timestamp { get; set; }
}
```

**Serialization Flow**:
```
ICommand → CommandStoreAdapter.Serialize → CommandData → ICommandStore.Append
```

**Deserialization Flow**:
```
ICommandStore.Load → CommandData → CommandStoreAdapter.Deserialize → ICommand
```

---

## Service Lifetimes

### Scoped Services (Per Request)

**Why**: Transaction boundaries, isolation

```csharp
// Command pipeline (transactional)
services.AddScoped<ICommandBus, CommandBus>();
services.AddScoped<ICommandDispatcher, CommandDispatcher>();
services.AddScoped<ICommandSubscriber, CommandSubscriber>();

// Store adapters (per-request isolation)
services.AddScoped<ICommandStoreAdapter, CommandStoreAdapter>();
services.AddScoped<IEntityStoreAdapter, EntityStoreAdapter>();
services.AddScoped<IViewModelStoreAdapter, ViewModelStoreAdapter>();
```

---

### Singleton Services (Stateless)

**Why**: Performance, thread-safe

```csharp
// Event pipeline (stateless)
services.AddSingleton<IEventQueue, EventQueue>();
services.AddSingleton<IEventDispatcher, EventDispatcher>();
services.AddSingleton<IEventSubscriber, Aggregate.EventSubscriber>();
services.AddSingleton<IEventSubscriber, Projections.EventSubscriber>();

// Observability
services.AddSingleton<IDomainTelemetryService, DomainTelemetryService>();
```

---

### Configurable Lifetime (Default: Singleton)

**Why**: User choice based on use case

```csharp
// Domain components
services.AddImplementationAsInterfaces<ISaga>(assemblies, ServiceLifetime.Singleton);
services.AddImplementationAsInterfaces<IAggregate>(assemblies, ServiceLifetime.Singleton);
services.AddImplementationAsInterfaces<IView>(assemblies, ServiceLifetime.Singleton);
```

---

## Extension Points

### 1. Add New ICommandDispatcher

**Use Case**: Send commands to AWS SQS, Azure Service Bus, etc.

```csharp
// Implement interface
public class AwsSqsCommandDispatcher : ICommandDispatcher
{
    public async Task Dispatch<TCommand>(TCommand command)
    {
        // Check routing configuration
        if (!ShouldRouteToAws<TCommand>()) return;

        // Send to SQS
        await sqsClient.SendMessageAsync(...);
    }
}

// Register
services.AddScoped<ICommandDispatcher, CommandDispatcher>();        // Local
services.AddScoped<ICommandDispatcher, AwsSqsCommandDispatcher>();  // AWS

// Now CommandBus has TWO dispatchers - both receive all commands
```

---

### 2. Add New IEventDispatcher

**Use Case**: Publish events to AWS SNS, Azure Service Bus Topics, etc.

```csharp
// Implement interface
public class AwsSnsEventDispatcher : IEventDispatcher
{
    public async Task Dispatch<TEvent>(TEvent @event)
    {
        // Check routing configuration
        if (!ShouldRouteToAws<TEvent>()) return;

        // Publish to SNS
        await snsClient.PublishAsync(...);
    }
}

// Register
services.AddSingleton<IEventDispatcher, EventDispatcher>();      // Local
services.AddSingleton<IEventDispatcher, AwsSnsEventDispatcher>(); // AWS

// Now EventQueue has TWO dispatchers - both receive all events
```

---

### 3. Implement Custom Stores

**Use Case**: Use MongoDB, Cosmos DB, DynamoDB, etc.

```csharp
// Implement interface
public class MongoDbCommandStore : ICommandStore
{
    public async Task Append(CommandData commandData)
    {
        await collection.InsertOneAsync(commandData);
    }

    public async Task<IEnumerable<CommandData>> Load(int entityId)
    {
        return await collection.Find(c => c.EntityId == entityId)
                               .SortBy(c => c.SequenceNo)
                               .ToListAsync();
    }
}

// Register
services.AddSingleton<ICommandStore, MongoDbCommandStore>();

// Adapter automatically wraps it
services.AddScoped<ICommandStoreAdapter, CommandStoreAdapter>();
```

---

### 4. Create New Sagas

**Use Case**: Implement business workflows

```csharp
public class OrderSaga : Saga<OrderEntity>,
                         IHandles<CreateOrderCommand>,
                         IHandles<ConfirmOrderCommand>
{
    public async Task<IEntity> Handle(IEntity entity, CreateOrderCommand command)
    {
        var order = entity as OrderEntity ?? new OrderEntity();
        order.Id = command.Entity.Id;
        order.Status = OrderStatus.Pending;

        await PublishEvent(new OrderCreatedEvent { OrderId = order.Id });
        return order;
    }

    public async Task<IEntity> Handle(IEntity entity, ConfirmOrderCommand command)
    {
        var order = entity as OrderEntity;
        order.Status = OrderStatus.Confirmed;

        await PublishEvent(new OrderConfirmedEvent { OrderId = order.Id });
        return order;
    }
}

// Auto-registered by UseSourceFlow()
```

---

### 5. Create Views (Projections)

**Use Case**: Materialize read models

```csharp
public class OrderSummaryView : View<OrderSummaryViewModel>,
                                 IProjectOn<OrderCreatedEvent>,
                                 IProjectOn<OrderConfirmedEvent>
{
    public async Task<IViewModel> On(OrderCreatedEvent @event)
    {
        var viewModel = await Load(@event.OrderId)
                     ?? new OrderSummaryViewModel { Id = @event.OrderId };

        viewModel.Status = "Created";
        viewModel.CreatedAt = DateTime.UtcNow;

        return await Save(viewModel);
    }

    public async Task<IViewModel> On(OrderConfirmedEvent @event)
    {
        var viewModel = await Load(@event.OrderId);
        viewModel.Status = "Confirmed";
        viewModel.ConfirmedAt = DateTime.UtcNow;

        return await Save(viewModel);
    }
}

// Auto-registered by UseSourceFlow()
```

---

## Key Design Principles

### 1. Open/Closed Principle

**Open for extension, closed for modification**

```csharp
// Adding AWS dispatcher doesn't modify CommandBus
services.AddScoped<ICommandDispatcher, AwsSqsCommandDispatcher>();

// CommandBus.Publish() code never changes
public async Task Publish<TCommand>(TCommand command)
{
    foreach (var dispatcher in commandDispatchers) // Extensible collection
        tasks.Add(dispatcher.Dispatch(command));
    await Task.WhenAll(tasks);
}
```

---

### 2. Separation of Concerns

**Each layer has a single responsibility**:

| Layer | Responsibility |
|-------|----------------|
| **Bus/Queue** | Orchestration, sequencing, persistence |
| **Dispatcher** | Routing strategy (local, cloud) |
| **Subscriber** | Type-based filtering |
| **Handler** | Business logic |

---

### 3. Interface Segregation

**Focused, cohesive interfaces**:

```csharp
// Sagas handle commands
public interface ISaga
{
    Task Handle<TCommand>(TCommand command) where TCommand : ICommand;
}

// Aggregates subscribe to events
public interface ISubscribes<in TEvent> where TEvent : IEvent
{
    Task On(TEvent @event);
}

// Views project events
public interface IProjectOn<TEvent> where TEvent : IEvent
{
    Task<IViewModel> On(TEvent @event);
}
```

Each interface has a clear, single purpose.

---

### 4. Dependency Inversion

**Depend on abstractions, not implementations**:

```csharp
// CommandBus depends on abstraction
public class CommandBus
{
    private readonly IEnumerable<ICommandDispatcher> dispatchers;
    private readonly ICommandStoreAdapter store;

    // Doesn't know about concrete implementations
}

// Concrete implementations registered at runtime
services.AddScoped<ICommandDispatcher, LocalCommandDispatcher>();
services.AddScoped<ICommandDispatcher, AwsCommandDispatcher>();
```

---

### 5. Type Safety

**Generic types preserved throughout**:

```csharp
// Type flows from top to bottom
ICommandBus.Publish<CreateOrderCommand>()
    → CommandDispatcher.Dispatch<CreateOrderCommand>()
        → CommandSubscriber.Subscribe<CreateOrderCommand>()
            → OrderSaga.Handle<CreateOrderCommand>()

// No type casting, no reflection (except replay)
```

---

### 6. Performance First

**Optimizations built-in**:

- **Parallel processing**: Multiple dispatchers/subscribers run concurrently
- **TaskBufferPool**: ArrayPool for task collections (reduces allocations)
- **ByteArrayPool**: Pooled buffers for serialization
- **AsNoTracking**: EF queries for read-only operations
- **Sender/Processor caching**: Reuse cloud client instances

---

## Quick Reference: Common Operations

### Publish a Command
```csharp
await commandBus.Publish(new CreateOrderCommand
{
    Entity = new EntityRef { Id = 123 },
    Payload = new CreateOrderCommandData { ... }
});
```

### Replay Commands (Event Sourcing)
```csharp
await commandBus.Replay(entityId: 123); // Reconstructs state
```

### Publish an Event
```csharp
// In Saga
await PublishEvent(new OrderCreatedEvent { OrderId = 123 });
```

### Query a View Model
```csharp
var order = await viewModelStore.Get<OrderViewModel>(123);
```

### Register SourceFlow
```csharp
services.UseSourceFlow(ServiceLifetime.Singleton, assemblies);
```

---

## Summary

**SourceFlow.Net provides**:

✅ **CQRS** - Separate read and write models
✅ **Event Sourcing** - Complete audit trail and state replay
✅ **Saga Pattern** - Complex workflow orchestration
✅ **Extensibility** - Plugin architecture via collections of dispatchers
✅ **Type Safety** - Generics preserved throughout
✅ **Performance** - Parallel processing and pooling optimizations
✅ **Observability** - Built-in telemetry and tracing
✅ **Cloud Ready** - Easy to add AWS, Azure, or multi-cloud support

**Extension Points**:
- Add new dispatchers (cloud messaging)
- Implement custom stores (NoSQL, cloud storage)
- Create sagas (business workflows)
- Create views (read model projections)

**Zero Core Modifications Required** for extensions!

---

## Next Steps

### Understanding Core Functionality
1. **Read Document 01** - Architecture Overview (high-level)
2. **Read Document 02** - Command Flow Analysis (deep dive)
3. **Read Document 03** - Event Flow Analysis (deep dive)
4. **Read Document 04** - Dispatching Patterns (extension points)
5. **Read Document 05** - Store Persistence (storage layer)

### Implementing Cloud Extensions
- **For AWS**: Read documents 06-07
- **For Azure**: Read documents 08-09
- **For Multi-Cloud**: Read all cloud documents

### Building with SourceFlow.Net
1. Define your domain entities
2. Create commands and events
3. Implement sagas for workflows
4. Create views for read models
5. Configure stores (SQL, NoSQL, etc.)
6. Optionally add cloud dispatchers

**The core is complete and production-ready. Cloud extensions are optional add-ons.**

---

## Related Documents

| Document | File | Purpose |
|----------|------|---------|
| Main README | `00-README.md` | Complete documentation index |
| 01 | `01-Architecture-Overview.md` | Detailed architecture |
| 02 | `02-Command-Flow-Analysis.md` | Command processing deep dive |
| 03 | `03-Event-Flow-Analysis.md` | Event processing deep dive |
| 04 | `04-Current-Dispatching-Patterns.md` | Extension points analysis |
| 05 | `05-Store-Persistence-Architecture.md` | Storage layer deep dive |
| 06 | `06-AWS-Cloud-Extension-Design.md` | AWS integration |
| 07 | `07-AWS-Implementation-Roadmap.md` | AWS implementation plan |
| 08 | `08-Azure-Cloud-Extension-Design.md` | Azure integration |
| 09 | `09-Azure-Implementation-Roadmap.md` | Azure implementation plan |

---

**Document Version**: 1.0
**Last Updated**: 2025-11-30
**Based On**: Analysis documents 01-05
