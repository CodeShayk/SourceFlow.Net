# Current Dispatching Patterns and Extension Points

## Overview
This document provides a detailed analysis of the existing dispatching patterns in SourceFlow.Net and identifies the specific extension points that enable cloud integration without modifying core code.

## Dispatcher Pattern Architecture

### Core Abstraction: Multiple Dispatchers

SourceFlow uses a collection-based dispatcher pattern:
- **CommandBus** accepts `IEnumerable<ICommandDispatcher>`
- **EventQueue** accepts `IEnumerable<IEventDispatcher>`

This design enables:
1. Multiple processing strategies simultaneously
2. Plugin architecture (add new dispatchers without changing core)
3. Separation of concerns (each dispatcher handles one strategy)

---

## Command Dispatching Pattern

### Current Implementation

**CommandBus.cs:66-100**

```csharp
private async Task Dispatch<TCommand>(TCommand command) where TCommand : ICommand
{
    await telemetry.TraceAsync(
        "sourceflow.commandbus.dispatch",
        async () =>
        {
            // 1. Set sequence number (if not replay)
            if (!command.Metadata.IsReplay)
                command.Metadata.SequenceNo = await commandStore.GetNextSequenceNo(command.Entity.Id);

            // 2. DISPATCH TO ALL COMMAND DISPATCHERS (Extension Point!)
            var tasks = new List<Task>();
            foreach (var dispatcher in commandDispatchers)
                tasks.Add(DispatchCommand(command, dispatcher));

            if (tasks.Any())
                await Task.WhenAll(tasks);

            // 3. Persist command (if not replay)
            if (!command.Metadata.IsReplay)
                await commandStore.Append(command);
        },
        // ... telemetry tags
    );
}

private Task DispatchCommand<TCommand>(TCommand command, ICommandDispatcher dispatcher)
    where TCommand : ICommand
{
    logger?.LogInformation("Action=Command_Dispatched, Dispatcher={Dispatcher}, ...",
        dispatcher.GetType().Name, ...);

    return dispatcher.Dispatch(command);
}
```

**Key Observations**:
1. **All dispatchers receive the same command**: Same instance, same sequence number
2. **Parallel execution**: All dispatchers run concurrently via `Task.WhenAll`
3. **Type preservation**: Generic `TCommand` maintained through pipeline
4. **Dispatcher independence**: One dispatcher failure doesn't affect others
5. **Logging per dispatcher**: Can trace which dispatcher handled command

---

### Current ICommandDispatcher Implementation

**CommandDispatcher.cs (the only current implementation)**

```csharp
internal class CommandDispatcher : ICommandDispatcher
{
    private readonly IEnumerable<ICommandSubscriber> commandSubscribers;

    public Task Dispatch<TCommand>(TCommand command) where TCommand : ICommand
    {
        return Send(command);
    }

    private Task Send<TCommand>(TCommand command) where TCommand : ICommand
    {
        return telemetry.TraceAsync(
            "sourceflow.commanddispatcher.send",
            async () =>
            {
                if (!commandSubscribers.Any())
                {
                    logger?.LogInformation("Message=No subscribers Found");
                    return;
                }

                // Dispatch to all command subscribers (including CommandSubscriber -> Sagas)
                await TaskBufferPool.ExecuteAsync(
                    commandSubscribers,
                    subscriber => subscriber.Subscribe(command));
            },
            // ... telemetry
        );
    }
}
```

**Flow**: `CommandDispatcher` → `CommandSubscriber` (collection) → `Sagas` (filtered by type)

---

### Extension Point: Add New ICommandDispatcher

**Registration** (IocExtensions.cs:79-80):
```csharp
services.AddScoped<ICommandDispatcher, CommandDispatcher>();
```

**AWS Extension** would add:
```csharp
services.AddScoped<ICommandDispatcher, CommandDispatcher>(); // Existing
services.AddScoped<ICommandDispatcher, AwsSqsCommandDispatcher>(); // NEW
```

**Result**:
- CommandBus now has TWO dispatchers in its collection
- Both receive every command
- Local dispatcher routes to Sagas
- AWS dispatcher sends to SQS (conditionally, based on routing config)

---

## Event Dispatching Pattern

### Current Implementation

**EventQueue.cs:50-72**

```csharp
public Task Enqueue<TEvent>(TEvent @event) where TEvent : IEvent
{
    if (@event == null)
        throw new ArgumentNullException(nameof(@event));

    return telemetry.TraceAsync(
        "sourceflow.eventqueue.enqueue",
        async () =>
        {
            // DISPATCH TO ALL EVENT DISPATCHERS (Extension Point!)
            var tasks = new List<Task>();
            foreach (var eventDispatcher in eventDispatchers)
                tasks.Add(DispatchEvent(@event, eventDispatcher));

            if (tasks.Any())
                await Task.WhenAll(tasks);
        },
        // ... telemetry
    );
}

private Task DispatchEvent<TEvent>(TEvent @event, IEventDispatcher eventDispatcher)
    where TEvent : IEvent
{
    logger?.LogInformation("Action=Event_Enqueue, Dispatcher={Dispatcher}, ...",
        eventDispatcher.GetType().Name, ...);

    return eventDispatcher.Dispatch(@event);
}
```

**Key Observations**:
1. **All dispatchers receive the same event**: Same instance
2. **Parallel execution**: All dispatchers run concurrently via `Task.WhenAll`
3. **Type preservation**: Generic `TEvent` maintained through pipeline
4. **No persistence in EventQueue**: Events are not stored (commands are)
5. **Logging per dispatcher**: Can trace which dispatcher handled event

---

### Current IEventDispatcher Implementation

**EventDispatcher.cs (the only current implementation)**

```csharp
internal class EventDispatcher : IEventDispatcher
{
    private IEnumerable<IEventSubscriber> subscribers;

    public Task Dispatch<TEvent>(TEvent @event) where TEvent : IEvent
    {
        return telemetry.TraceAsync(
            "sourceflow.eventdispatcher.dispatch",
            async () =>
            {
                // Dispatch to all event subscribers (Aggregate and Projections subscribers)
                await TaskBufferPool.ExecuteAsync(
                    subscribers,
                    subscriber =>
                    {
                        logger?.LogInformation("Action=Event_Dispatcher, ...");
                        return subscriber.Subscribe(@event);
                    });
            },
            // ... telemetry
        );
    }
}
```

**Flow**: `EventDispatcher` → `IEventSubscriber[]` → `[Aggregate.EventSubscriber, Projections.EventSubscriber]` → `[Aggregates, Views]`

---

### Extension Point: Add New IEventDispatcher

**Registration** (IocExtensions.cs:97-98):
```csharp
services.AddSingleton<IEventDispatcher, EventDispatcher>();
services.AddSingleton<IEventQueue, EventQueue>();
```

**AWS Extension** would add:
```csharp
services.AddSingleton<IEventDispatcher, EventDispatcher>(); // Existing
services.AddSingleton<IEventDispatcher, AwsSnsEventDispatcher>(); // NEW
```

**Result**:
- EventQueue now has TWO dispatchers in its collection
- Both receive every event
- Local dispatcher routes to Aggregates/Views
- AWS dispatcher publishes to SNS (conditionally, based on routing config)

---

## Subscriber Pattern (Not an Extension Point for Cloud)

### Command Subscribers

**Current**: `CommandSubscriber` implements `ICommandSubscriber`
- Registered in `CommandDispatcher` (not CommandBus)
- Routes to Sagas based on type capability

**Cloud Listener**:
- Does NOT implement `ICommandSubscriber`
- Instead, directly invokes existing `ICommandSubscriber` after receiving from SQS
- Reuses existing saga routing logic

**Why**?
- Cloud listener is not in the dispatch pipeline
- It's a separate background service that receives from SQS
- It then routes to existing `ICommandSubscriber` to leverage saga filtering

---

### Event Subscribers

**Current**: Two implementations of `IEventSubscriber`
1. `Aggregate.EventSubscriber` - Routes to aggregates
2. `Projections.EventSubscriber` - Routes to views

**Cloud Listener**:
- Does NOT implement `IEventSubscriber`
- Instead, gets all `IEventSubscriber` instances and invokes them
- Reuses existing aggregate/view routing logic

**Why**?
- Cloud listener is not in the dispatch pipeline
- It's a separate background service that receives from SNS/SQS
- It then routes to existing `IEventSubscriber[]` to leverage type filtering

---

## Selective Routing Strategy

### Concept
Not all commands/events should go to AWS. Need selective routing based on:
1. Command/Event type
2. Attributes on command/event classes
3. Configuration settings

### Implementation in Dispatcher

**AwsSqsCommandDispatcher.Dispatch() pseudocode**:
```csharp
public async Task Dispatch<TCommand>(TCommand command) where TCommand : ICommand
{
    // 1. CHECK IF THIS COMMAND SHOULD BE ROUTED TO AWS
    if (!routingConfig.ShouldRouteToAws<TCommand>())
    {
        // This command is local-only, skip this dispatcher
        return;
    }

    // 2. THIS COMMAND SHOULD GO TO AWS, PROCEED WITH SQS SEND
    var queueUrl = routingConfig.GetQueueUrl<TCommand>();
    // ... serialize and send to SQS
}
```

**Result**:
- Dispatcher is ALWAYS invoked (it's in the collection)
- Dispatcher decides internally whether to process the command
- Early return if command should not be routed
- Only commands marked for AWS actually get sent

**Benefit**:
- No conditional registration (cleaner IoC)
- Routing logic encapsulated in dispatcher
- Can change routing without changing CommandBus

---

## Hybrid Processing Pattern

### Scenario: Both Local and Cloud

**Setup**:
1. `CommandDispatcher` (local) - Always routes to Sagas
2. `AwsSqsCommandDispatcher` (cloud) - Routes to SQS if configured

**Command Processing**:
```
CreateOrderCommand received
  │
  ▼
CommandBus.Dispatch()
  │
  ├─► CommandDispatcher
  │     └─► CommandSubscriber
  │           └─► OrderSaga.Handle() [LOCAL PROCESSING]
  │
  └─► AwsSqsCommandDispatcher
        └─► if ShouldRouteToAws():
              └─► Send to SQS [CLOUD PROCESSING]
```

**Result**:
- Command handled locally immediately
- Also sent to SQS for cloud processing
- Cloud service receives from SQS and processes again
- **Idempotency required**: Saga must handle duplicate commands

---

### Scenario: Selective Routing (Some Local, Some Cloud)

**Setup**:
1. `CreateOrderCommand` - Marked for local processing only
2. `ProcessPaymentCommand` - Marked for AWS processing
3. Both dispatchers registered

**CreateOrderCommand Processing**:
```
CreateOrderCommand received
  │
  ▼
CommandBus.Dispatch()
  │
  ├─► CommandDispatcher
  │     └─► Routes to OrderSaga [PROCESSED]
  │
  └─► AwsSqsCommandDispatcher
        └─► ShouldRouteToAws<CreateOrderCommand>() == false
              └─► Early return [SKIPPED]
```

**ProcessPaymentCommand Processing**:
```
ProcessPaymentCommand received
  │
  ▼
CommandBus.Dispatch()
  │
  ├─► CommandDispatcher
  │     └─► Routes to PaymentSaga [PROCESSED LOCALLY if needed]
  │
  └─► AwsSqsCommandDispatcher
        └─► ShouldRouteToAws<ProcessPaymentCommand>() == true
              └─► Send to SQS [SENT TO CLOUD]
```

**Result**:
- Each command type can have different routing
- No code changes to CommandBus
- Configuration/attribute-driven

---

## Listener Pattern (Cloud → Local)

### Command Listener Flow

```
[AWS SQS Queue]
     │
     │ Long polling
     ▼
AwsSqsCommandListener (Background Service)
     │
     │ Receive message
     │ Parse CommandType from attributes
     │ Deserialize JSON to ICommand
     │
     │ Create scoped service provider
     │
     ▼
ICommandSubscriber.Subscribe<TCommand>(command)
     │
     │ [Existing saga routing logic from here]
     │
     ▼
CommandSubscriber
     └─► Routes to appropriate Saga based on type
           └─► Saga.Handle<TCommand>()
```

**Key Points**:
1. Listener is a **BackgroundService** (not a dispatcher)
2. Receives messages from SQS independently
3. Invokes existing `ICommandSubscriber` to leverage routing
4. Sagas don't know if command came from local or cloud

---

### Event Listener Flow

```
[AWS SNS Topic]
     │
     │ Fan-out
     ▼
[AWS SQS Queue] (SNS subscription)
     │
     │ Long polling
     ▼
AwsSnsEventListener (Background Service)
     │
     │ Receive message
     │ Parse SNS notification wrapper
     │ Get EventType from attributes
     │ Deserialize JSON to IEvent
     │
     │ Get all IEventSubscriber instances (singleton)
     │
     ▼
IEventSubscriber[].Subscribe<TEvent>(@event)
     │
     │ [Existing aggregate/view routing logic from here]
     │
     ├─► Aggregate.EventSubscriber
     │     └─► Routes to Aggregates implementing ISubscribes<TEvent>
     │           └─► Aggregate.On(@event)
     │
     └─► Projections.EventSubscriber
           └─► Routes to Views implementing IProjectOn<TEvent>
                 └─► View.On(@event) → Update read model
```

**Key Points**:
1. Listener is a **BackgroundService** (not a dispatcher)
2. Receives messages from SQS (which subscribes to SNS)
3. Invokes ALL existing `IEventSubscriber` instances
4. Aggregates/Views don't know if event came from local or cloud

---

## Extension Point Summary

### What to Extend

| Component | Interface | Purpose | Lifetime | Registration |
|-----------|-----------|---------|----------|--------------|
| **Command Dispatcher** | `ICommandDispatcher` | Send commands (local or cloud) | Scoped | `services.AddScoped<ICommandDispatcher, AwsSqsCommandDispatcher>()` |
| **Event Dispatcher** | `IEventDispatcher` | Send events (local or cloud) | Singleton | `services.AddSingleton<IEventDispatcher, AwsSnsEventDispatcher>()` |
| **Command Listener** | `BackgroundService` | Receive commands from cloud | Singleton | `services.AddHostedService<AwsSqsCommandListener>()` |
| **Event Listener** | `BackgroundService` | Receive events from cloud | Singleton | `services.AddHostedService<AwsSnsEventListener>()` |

### What NOT to Modify

| Component | Why Not |
|-----------|---------|
| **CommandBus** | Already supports multiple dispatchers via `IEnumerable<ICommandDispatcher>` |
| **EventQueue** | Already supports multiple dispatchers via `IEnumerable<IEventDispatcher>` |
| **CommandSubscriber** | Local saga routing works perfectly, reuse it from cloud listener |
| **EventSubscriber** (both) | Local aggregate/view routing works perfectly, reuse it from cloud listener |
| **ISaga** implementations | Domain logic should be cloud-agnostic |
| **IAggregate** implementations | Domain logic should be cloud-agnostic |
| **IView** implementations | Domain logic should be cloud-agnostic |

---

## Design Principles Enabling Cloud Extension

### 1. Dependency Injection and Collections

**Code** (CommandBus.cs:29):
```csharp
public IEnumerable<ICommandDispatcher> commandDispatchers;
```

**Why It Matters**:
- IoC container injects ALL registered `ICommandDispatcher` implementations
- Can add new implementations without changing CommandBus
- True plugin architecture

---

### 2. Interface-Based Design

**All major components use interfaces**:
- `ICommandBus`, `IEventQueue` (entry points)
- `ICommandDispatcher`, `IEventDispatcher` (routing)
- `ICommandSubscriber`, `IEventSubscriber` (subscription)

**Why It Matters**:
- Easy to mock for testing
- Easy to add new implementations
- Clear contracts for extension

---

### 3. Generic Type Preservation

**Code** (CommandBus.cs:58):
```csharp
Task ICommandBus.Publish<TCommand>(TCommand command) where TCommand : ICommand
```

**Why It Matters**:
- Type information flows through entire pipeline
- Can serialize/deserialize with full type fidelity
- Enables cloud transport without losing type safety

---

### 4. Metadata on Messages

**IMetadata** (SequenceNo, IsReplay):
- Commands carry sequence numbers
- Enables ordering and replay

**IName** (Event names):
- Events have string names
- Useful for routing and filtering

**Why It Matters**:
- Can include metadata in cloud messages
- Enables distributed tracing
- Supports message ordering

---

### 5. Separation of Concerns

**Dispatchers** vs **Subscribers**:
- Dispatchers: "How to send" (local, cloud)
- Subscribers: "Who handles" (sagas, aggregates, views)

**Why It Matters**:
- Cloud extension adds dispatchers (how to send)
- Reuses subscribers (who handles)
- Domain logic unchanged

---

## Comparison with Alternative Architectures

### If SourceFlow Used Direct Saga Invocation

**Hypothetical Code**:
```csharp
// Bad design: Direct coupling
public class CommandBus
{
    private readonly ISaga saga;

    public Task Publish<TCommand>(TCommand command)
    {
        return saga.Handle(command); // Direct call
    }
}
```

**Problems for Cloud Extension**:
- Where to add cloud send logic?
- Would need to modify CommandBus
- Can't have both local and cloud simultaneously

---

### If SourceFlow Used Single Dispatcher

**Hypothetical Code**:
```csharp
public class CommandBus
{
    private readonly ICommandDispatcher dispatcher; // Single, not IEnumerable

    public Task Publish<TCommand>(TCommand command)
    {
        return dispatcher.Dispatch(command);
    }
}
```

**Problems for Cloud Extension**:
- Would need conditional logic in dispatcher
- Or create a "routing dispatcher" wrapping multiple dispatchers
- Less clean than native collection support

---

### Current Design: Collection of Dispatchers

**Actual Code**:
```csharp
public class CommandBus
{
    private readonly IEnumerable<ICommandDispatcher> commandDispatchers;

    public Task Publish<TCommand>(TCommand command)
    {
        foreach (var dispatcher in commandDispatchers)
            tasks.Add(dispatcher.Dispatch(command));
        await Task.WhenAll(tasks);
    }
}
```

**Benefits for Cloud Extension**:
- Add new dispatcher via IoC registration
- Zero changes to CommandBus
- Each dispatcher is independent
- Easy to enable/disable via registration

---

## Summary

SourceFlow's architecture enables cloud extension because:

1. **Plugin Architecture**: Collections of dispatchers, not single instance
2. **Interface Segregation**: Clear separation between dispatch, subscribe, and handle
3. **Generic Type Flow**: Type information preserved for serialization
4. **Parallel Processing**: Multiple dispatchers execute concurrently
5. **Conditional Logic in Dispatcher**: Routing decisions made in dispatcher, not bus
6. **Reusable Subscribers**: Cloud listeners invoke existing subscribers

**Extension Strategy**:
- **Add** new ICommandDispatcher (AWS SQS sender)
- **Add** new IEventDispatcher (AWS SNS publisher)
- **Add** new BackgroundService (SQS/SNS listeners)
- **Reuse** all existing subscribers (CommandSubscriber, EventSubscribers)
- **Don't modify** CommandBus, EventQueue, or domain logic

This is a textbook example of the **Open/Closed Principle**: Open for extension, closed for modification.
