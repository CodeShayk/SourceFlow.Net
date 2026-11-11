# Event Flow Analysis

## Overview
Events in SourceFlow represent facts about state changes that have occurred. They flow from event producers (typically Sagas) through the EventQueue to multiple subscribers (Aggregates and Views).

## Event Flow Sequence

### Complete Flow Diagram
```
Saga/Command Handler
  │
  │ 1. Enqueue<TEvent>(event)
  ▼
IEventQueue (EventQueue.cs:50)
  │
  │ Telemetry: sourceflow.eventqueue.enqueue
  │
  │ 2. For Each IEventDispatcher
  │   │
  │   │ 3. DispatchEvent<TEvent>()
  │   ▼
  │   IEventDispatcher.Dispatch (EventDispatcher.cs:49)
  │     │
  │     │ Telemetry: sourceflow.eventdispatcher.dispatch
  │     │
  │     │ 4. For Each IEventSubscriber
  │     │
  │     ▼
  │     ┌──────────────────────────────────────────────┐
  │     │         IEventSubscriber.Subscribe           │
  │     └──────────────────┬───────────────────────────┘
  │                        │
  │     ┌──────────────────┴───────────────────┐
  │     │                                      │
  │     ▼                                      ▼
  │  Aggregate.EventSubscriber            Projections.EventSubscriber
  │  (EventSubscriber.cs:47)              (EventSubscriber.cs:47)
  │     │                                      │
  │     │ 5a. For Each IAggregate              │ 5b. For Each IView
  │     │     implementing                     │     implementing
  │     │     ISubscribes<TEvent>              │     IProjectOn<TEvent>
  │     │                                      │
  │     ▼                                      ▼
  │  ISubscribes<TEvent>.On(@event)       IProjectOn<TEvent>.On(@event)
  │     │                                      │
  │     ▼                                      ▼
  │  Aggregate Updates Internal State     View Updates Read Model
  │  (No persistence - event sourced)     (Persists to IViewModelStore)
  │
  └─► Returns Task
```

## Detailed Step-by-Step Analysis

### Step 1: Event Publication (IEventQueue.Enqueue)
**Location**: `EventQueue.cs:50-72`

```csharp
public Task Enqueue<TEvent>(TEvent @event)
    where TEvent : IEvent
{
    if (@event == null)
        throw new ArgumentNullException(nameof(@event));

    return telemetry.TraceAsync(
        "sourceflow.eventqueue.enqueue",
        async () =>
        {
            var tasks = new List<Task>();
            foreach (var eventDispatcher in eventDispatchers)
                tasks.Add(DispatchEvent(@event, eventDispatcher));

            if (tasks.Any())
                await Task.WhenAll(tasks);
        },
        // ... tags
    );
}
```

**Responsibilities**:
- Entry point for all events
- Null validation
- Telemetry tracking
- Parallel dispatch to all IEventDispatcher instances

**Key Interfaces**: `IEventQueue` (IEventQueue.cs:5-16)

**Telemetry Tags**:
- `event.type`: Type name of the event
- `event.name`: Event name (IName.Name property)

**Logging**:
- `Action=Event_Enqueue` for each dispatcher

---

### Step 2: Event Dispatcher Routing (EventDispatcher.Dispatch)
**Location**: `EventDispatcher.cs:49-72`

**Telemetry**: Wrapped in `sourceflow.eventdispatcher.dispatch` trace

**Flow**:
1. Uses **TaskBufferPool.ExecuteAsync** for optimized parallel execution
2. For each IEventSubscriber:
   - Logs dispatcher action
   - Calls `subscriber.Subscribe(@event)`

**Performance Optimization**:
- `TaskBufferPool` reduces memory allocations
- Parallel execution of all subscribers

**Tags Set**:
- `event.type`: Type name of the event
- `event.name`: Event name
- `subscribers.count`: Number of event subscribers

**Logging**:
- `Action=Event_Dispatcher` for each subscriber

---

### Step 3a: Aggregate Event Subscription (Aggregate.EventSubscriber)
**Location**: `SourceFlow.Net\src\SourceFlow\Aggregate\EventSubscriber.cs:47-63`

**Purpose**: Route events to aggregates that can handle them

**Flow**:
1. **Type Checking**
   - For each IAggregate in registered aggregates
   - Check if aggregate implements `ISubscribes<TEvent>`
   - Cast to `ISubscribes<TEvent>` if match found

2. **Event Application**
   - Call `eventSubscriber.On(@event)`
   - Aggregate updates its internal state

3. **Parallel Execution**
   - Creates task for each matching aggregate
   - Executes `Task.WhenAll(tasks)`

**Logging**:
- `Action=Event_Disptcher_Aggregate` for each matching aggregate

**Key Interface**: `ISubscribes<TEvent>` (ISubscribes.cs:10-20)
```csharp
public interface ISubscribes<in TEvent>
    where TEvent : IEvent
{
    Task On(TEvent @event);
}
```

**Important Note**:
- Aggregates using event sourcing do NOT persist state here
- They update in-memory state from events
- State is reconstructed from events on load

---

### Step 3b: View Event Subscription (Projections.EventSubscriber)
**Location**: `SourceFlow.Net\src\SourceFlow\Projections\EventSubscriber.cs:47-71`

**Purpose**: Route events to views (read models) for materialization

**Flow**:
1. **Check for Views**
   - If no views registered, logs and returns

2. **View Filtering**
   - For each IView in registered views
   - Checks `View<IViewModel>.CanHandle(view, @event.GetType())`
   - Only routes to views that can handle the event type

3. **Event Projection**
   - Call `view.Apply(@event)`
   - View updates read model (ViewModel)
   - View persists to IViewModelStore

4. **Parallel Execution**
   - Creates task for each matching view
   - Executes `Task.WhenAll(tasks)`

**Logging**:
- `Action=Command_Dispatcher` when no views found (log message appears incorrect)
- `Action=Projection_Apply` for each matching view
  - Includes: Event type, View type, SequenceNo

**Key Interface**: `IProjectOn<TEvent>` (IProjectOn.cs:10-20)
```csharp
public interface IProjectOn<TEvent>
    where TEvent : IEvent
{
    Task<IViewModel> On(TEvent @event);
}
```

**Important Note**:
- Views MUST persist updated read models
- Read models are queryable by clients
- Enables CQRS query side

---

## Event Subscriber Registration

### Service Registration (IocExtensions.cs:93-98)
```csharp
// Register event subscribers as singleton services
services.AddSingleton<IEventSubscriber, Aggregate.EventSubscriber>();
services.AddSingleton<IEventSubscriber, Projections.EventSubscriber>();

services.AddSingleton<IEventDispatcher, EventDispatcher>();
services.AddSingleton<IEventQueue, EventQueue>();
```

**Key Points**:
- **Two EventSubscriber implementations** registered by default:
  1. `Aggregate.EventSubscriber` - Routes to IAggregate implementations
  2. `Projections.EventSubscriber` - Routes to IView implementations

- **Singleton Lifetime**: Event processing is stateless
  - Aggregates and Views handle their own state
  - Subscribers just route based on type

- **EventDispatcher receives both subscribers**:
  - Constructor: `IEnumerable<IEventSubscriber> subscribers`
  - Both aggregate and projection subscribers in the collection

---

## Type-Based Routing

### Aggregate Subscription Check
**Pattern**: Direct type checking with interface cast

```csharp
if (!(aggregate is ISubscribes<TEvent> eventSubscriber))
    continue;

tasks.Add(eventSubscriber.On(@event));
```

**Advantages**:
- Simple and efficient
- Compile-time type safety
- No reflection needed at runtime

---

### View Subscription Check
**Pattern**: Reflection-based capability check

**Location**: Uses `View<IViewModel>.CanHandle()` method

```csharp
if (view == null || !View<IViewModel>.CanHandle(view, @event.GetType()))
    continue;

tasks.Add(view.Apply(@event));
```

**Why Reflection Here?**:
- Views may implement multiple `IProjectOn<TEvent>` interfaces
- CanHandle checks if view has the appropriate interface
- More flexible than compile-time checking

---

## Event Producer: Saga/Aggregate Integration

### How Events Are Created

Typical pattern in Saga command handlers:
1. Saga receives command via `ISaga.Handle<TCommand>()`
2. Saga loads entity state
3. Saga invokes domain logic: `IHandles<TCommand>.Handle(entity, command)`
4. Domain logic returns updated entity
5. **Saga publishes events** via `ICommandPublisher.PublishEvent<TEvent>()`
6. Event flows to IEventQueue

### ICommandPublisher Role
Sagas receive `Lazy<ICommandPublisher>` to break circular dependencies:
- ICommandPublisher internally uses IEventQueue
- Provides convenient API for sagas to publish events

---

## Event Metadata

Events implement `IMetadata`:
- **SequenceNo**: Order of event (inherited from triggering command)
- Used for ordering and replay scenarios

Events also implement `IName`:
- **Name**: String identifier for the event type
- Useful for logging and external system integration

---

## Concurrency and Parallelism

### Multiple Levels of Parallelism

1. **Dispatcher Level** (EventQueue.cs:61-62)
   - Multiple IEventDispatcher instances execute in parallel
   - Currently only one dispatcher registered
   - Extensible for future routing strategies

2. **Subscriber Level** (EventDispatcher.cs:56-64)
   - All IEventSubscriber instances execute in parallel
   - Aggregate and Projection subscribers run concurrently
   - TaskBufferPool optimization

3. **Handler Level** (EventSubscriber implementations)
   - Multiple aggregates handling same event execute in parallel
   - Multiple views handling same event execute in parallel

### Thread Safety Considerations

**Aggregates**:
- Event handlers should be idempotent
- State updates should be deterministic
- No shared mutable state between aggregates

**Views**:
- May write to same view model from different events
- Must handle concurrent updates (optimistic concurrency, etc.)
- IViewModelStore must support thread-safe writes

---

## Performance Optimizations

### TaskBufferPool Usage
**Location**: EventDispatcher.cs:56

Replaces standard pattern:
```csharp
// Old pattern
var tasks = new List<Task>();
foreach (var subscriber in subscribers)
    tasks.Add(subscriber.Subscribe(@event));
await Task.WhenAll(tasks);

// New optimized pattern
await TaskBufferPool.ExecuteAsync(
    subscribers,
    subscriber => subscriber.Subscribe(@event));
```

**Benefits**:
- Reuses task arrays from ArrayPool
- Reduces GC pressure
- Especially beneficial with many subscribers

---

## Extension Points for AWS Cloud Integration

### Current Architecture Enables:

1. **New IEventDispatcher Implementation**
   - `AwsSnsEventDispatcher` alongside existing `EventDispatcher`
   - Receives same events from EventQueue
   - Publishes to SNS topic

2. **New IEventSubscriber Implementation**
   - `AwsSnsEventListener` subscribes to SNS topics
   - Receives events from SQS queue (SNS -> SQS subscription)
   - Deserializes events
   - Routes to existing Aggregate/View infrastructure

3. **Selective Routing by Event Type**
   - Conditional dispatcher registration
   - Attribute-based routing `[PublishToCloud]`
   - Configuration-based topic mapping

4. **Hybrid Processing**
   - Local EventDispatcher processes in-process
   - AWS EventDispatcher sends to cloud
   - Both can run simultaneously for same event

### Integration Points
- **EventQueue.cs:61-62**: Iterate through dispatchers
- **EventDispatcher.cs:56-64**: TaskBufferPool execution of subscribers
- **IocExtensions.cs:93-98**: Service registration

---

## Comparison: Events vs Commands

| Aspect | Commands | Events |
|--------|----------|--------|
| **Intent** | Change state | Fact about change |
| **Subscribers** | Sagas | Aggregates + Views |
| **Storage** | Persisted in CommandStore | Not stored (but trigger persistence) |
| **Replay** | Yes, via CommandBus.Replay | No direct replay |
| **Routing** | Via ICommandSubscriber | Via IEventSubscriber |
| **Service Lifetime** | Scoped (transactional) | Singleton (stateless) |
| **Parallelism** | Sagas execute in parallel | Aggregates & Views in parallel |

---

## Summary

The event flow is a broadcast pipeline with multiple subscriber types:
1. **EventQueue**: Orchestrates event distribution
2. **EventDispatcher**: Routes to all subscribers in parallel
3. **EventSubscriber (Aggregate)**: Updates aggregate internal state
4. **EventSubscriber (Projections)**: Materializes read models

This design provides:
- **Fan-out**: Single event reaches multiple handlers
- **Separation**: Aggregate state vs. Read model updates
- **CQRS**: Events drive the query side (views)
- **Extensibility**: Easy to add new subscriber types
- **Cloud Readiness**: Can add cloud-based dispatchers/subscribers
- **Performance**: Parallel processing with optimizations
