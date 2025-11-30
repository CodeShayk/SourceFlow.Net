# Command Flow Analysis

## Overview
Commands in SourceFlow represent intentions to change system state. They flow through a well-defined pipeline from publication to saga execution.

## Command Flow Sequence

### Complete Flow Diagram
```
Client
  │
  │ 1. Publish<TCommand>(command)
  ▼
ICommandBus (CommandBus.cs:58)
  │
  │ 2. Dispatch<TCommand>(command)
  ▼
CommandBus.Dispatch (CommandBus.cs:66-100)
  │
  ├─► Get Next Sequence Number (if not replay)
  │   └─► ICommandStoreAdapter.GetNextSequenceNo()
  │
  ├─► For Each ICommandDispatcher
  │   │
  │   │ 3. DispatchCommand<TCommand>()
  │   ▼
  │   ICommandDispatcher.Dispatch (CommandDispatcher.cs:48)
  │     │
  │     │ 4. Send<TCommand>(command)
  │     ▼
  │     CommandDispatcher.Send (CommandDispatcher.cs:59-84)
  │       │
  │       │ 5. For Each ICommandSubscriber
  │       │
  │       ▼
  │       ICommandSubscriber.Subscribe (CommandSubscriber.cs:41)
  │         │
  │         │ 6. For Each ISaga
  │         │    └─► Check if saga can handle command type
  │         │        └─► Saga<IEntity>.CanHandle()
  │         │
  │         │ 7. Send<TCommand>(saga, command)
  │         ▼
  │         CommandSubscriber.Send (CommandSubscriber.cs:70)
  │           │
  │           │ 8. ISaga.Handle<TCommand>(command)
  │           ▼
  │           Saga Implementation
  │             │
  │             │ - Loads entity state
  │             │ - Invokes IHandles<TCommand>.Handle()
  │             │ - Publishes events via ICommandPublisher
  │             │ - Saves updated entity
  │             ▼
  │           Returns Task
  │
  └─► Append Command to Store (if not replay)
      └─► ICommandStoreAdapter.Append(command)
```

## Detailed Step-by-Step Analysis

### Step 1: Command Publication (ICommandBus.Publish)
**Location**: `CommandBus.cs:58-64`

```csharp
Task ICommandBus.Publish<TCommand>(TCommand command)
{
    if (command == null)
        throw new ArgumentNullException(nameof(command));

    return Dispatch(command);
}
```

**Responsibilities**:
- Entry point for all commands
- Null validation
- Delegates to internal Dispatch method

**Key Interfaces**: `ICommandBus` (ICommandBus.cs:9-26)

---

### Step 2: Command Dispatch Orchestration (CommandBus.Dispatch)
**Location**: `CommandBus.cs:66-100`

**Telemetry**: Wrapped in `sourceflow.commandbus.dispatch` trace

**Flow**:
1. **Sequence Number Assignment** (if not replay)
   - Calls `commandStore.GetNextSequenceNo(command.Entity.Id)`
   - Sets `command.Metadata.SequenceNo`
   - Ensures ordering for event sourcing

2. **Parallel Dispatch to All Dispatchers**
   - Iterates through `IEnumerable<ICommandDispatcher>`
   - Creates task per dispatcher
   - Executes `Task.WhenAll(tasks)` for parallel processing

3. **Command Persistence** (if not replay)
   - Appends command to CommandStore
   - Only after successful dispatch
   - Enables command replay capability

**Tags Set**:
- `command.type`: Type name of the command
- `command.entity_id`: Target entity ID
- `command.sequence_no`: Assigned sequence number
- `command.is_replay`: Whether this is a replay

**Metrics**: Records command execution via `telemetry.RecordCommandExecuted()`

---

### Step 3: Dispatcher Routing (CommandDispatcher.Dispatch)
**Location**: `CommandDispatcher.cs:48-51`

```csharp
public Task Dispatch<TCommand>(TCommand command) where TCommand : ICommand
{
    return Send(command);
}
```

**Responsibilities**:
- Simple delegation to Send method
- Maintains interface contract

---

### Step 4: Subscriber Distribution (CommandDispatcher.Send)
**Location**: `CommandDispatcher.cs:59-84`

**Telemetry**: Wrapped in `sourceflow.commanddispatcher.send` trace

**Flow**:
1. Checks if any command subscribers exist
2. Uses **TaskBufferPool.ExecuteAsync** for optimized parallel execution
3. Calls `subscriber.Subscribe(command)` for each ICommandSubscriber

**Performance Optimization**:
- `TaskBufferPool` uses ArrayPool to reduce allocations
- Parallel execution of all subscribers

**Tags Set**:
- `command.type`: Type name of the command
- `command.entity_id`: Target entity ID
- `subscribers.count`: Number of command subscribers

---

### Step 5: Saga Subscription (CommandSubscriber.Subscribe)
**Location**: `CommandSubscriber.cs:41-61`

**Flow**:
1. **Check for Registered Sagas**
   - If no sagas, logs and returns completed task

2. **Saga Filtering**
   - For each saga in `IEnumerable<ISaga> sagas`
   - Checks `Saga<IEntity>.CanHandle(saga, command.GetType())`
   - Only routes to sagas that can handle the command type

3. **Parallel Saga Execution**
   - Creates task per matching saga
   - Executes `Task.WhenAll(tasks)`

**Logging**:
- `Action=Command_Dispatcher` when no sagas found
- `Action=Command_Dispatcher_Send` for each saga invocation

---

### Step 6: Saga Command Handling (Saga.Handle)
**Location**: Implementation-specific (user-defined sagas)

**Interface**: `ISaga.Handle<TCommand>` (ISaga.cs:26-28)

**Typical Saga Implementation Pattern**:
1. Load entity from IEntityStoreAdapter
2. Invoke domain logic implementing `IHandles<TCommand>`
3. Generate events based on command
4. Publish events via `ICommandPublisher.PublishEvent()`
5. Save updated entity state

**Key Interface**: `IHandles<TCommand>` (IHandles.cs:10-20)
```csharp
Task<IEntity> Handle(IEntity entity, TCommand command);
```

---

## Command Replay Flow

### Replay Trigger: ICommandBus.Replay
**Location**: `CommandBus.cs:124-151`

**Purpose**: Reconstruct aggregate state by replaying all commands

**Flow**:
1. Load all commands for entity: `commandStore.Load(entityId)`
2. For each command:
   - Set `command.Metadata.IsReplay = true`
   - Use reflection to invoke generic `Dispatch<TCommand>` method
   - Ensures type safety during replay

3. Dispatch processes as normal, BUT:
   - Skips sequence number assignment (uses stored sequence)
   - Skips command persistence (already persisted)

**Telemetry**: Wrapped in `sourceflow.commandbus.replay` trace

**Tags**:
- `entity_id`: ID of aggregate being replayed

---

## Command Store Integration

### Storage Operations

1. **GetNextSequenceNo**
   - Called before each command dispatch
   - Ensures command ordering per entity

2. **Append**
   - Called after successful dispatch
   - Persists command for replay
   - Not called during replay

3. **Load**
   - Called during replay
   - Returns all commands for an entity in sequence order

### Adapter Pattern
**ICommandStoreAdapter** (Scoped) wraps **ICommandStore** (Singleton/Scoped)
- Provides lifecycle management
- Enables transaction support

---

## Command Subscriber Registration

### Discovery Process (IocExtensions.cs:79-80)
```csharp
services.AddScoped<ICommandSubscriber, CommandSubscriber>();
services.AddScoped<ICommandDispatcher, CommandDispatcher>();
```

**Current Implementation**:
- Single `CommandSubscriber` registered
- Routes to all `ISaga` instances
- Sagas discovered via reflection and registered automatically

**Extension Points**:
- Additional `ICommandSubscriber` implementations can be registered
- All subscribers receive all commands
- Enables multi-tenancy, routing, filtering

---

## Type Safety and Reflection

### Generic Type Preservation
Commands maintain their generic type throughout the pipeline:
- `ICommandBus.Publish<TCommand>`
- `CommandDispatcher.Dispatch<TCommand>`
- `ICommandSubscriber.Subscribe<TCommand>`
- `ISaga.Handle<TCommand>`

### Reflection Usage
**Only in Replay**: Generic method invocation via reflection (CommandBus.cs:141-144)
```csharp
var dispatchMethod = this.GetType().GetMethod(nameof(Dispatch), ...);
var genericDispatchMethod = dispatchMethod.MakeGenericMethod(commandType);
await (Task)genericDispatchMethod.Invoke(this, new object[] { command });
```

---

## Concurrency and Thread Safety

### Parallel Processing
- Multiple dispatchers execute concurrently
- Multiple subscribers execute concurrently
- Multiple sagas execute concurrently

### Potential Issues
- Saga implementations must be thread-safe OR
- Ensure single saga instance per command via proper lifetime management

### Store Adapters
- Registered as **Scoped** to ensure per-request isolation
- Prevents cross-request state contamination

---

## Extension Points for AWS Cloud Integration

### Current Architecture Enables:

1. **New ICommandDispatcher Implementation**
   - `AwsSqsCommandDispatcher` alongside existing `CommandDispatcher`
   - Receives same commands from CommandBus
   - Can publish to SQS queue

2. **New ICommandSubscriber Implementation**
   - `AwsSqsCommandListener` polls SQS queue
   - Deserializes commands
   - Routes to existing Saga infrastructure

3. **Selective Routing**
   - Conditional dispatcher registration based on command type
   - Attribute-based or configuration-based routing
   - Can maintain hybrid local/cloud processing

### Integration Points
- **CommandBus.cs:79-80**: Iterate through dispatchers
- **CommandDispatcher.cs:74-76**: TaskBufferPool execution of subscribers
- **IocExtensions.cs:79-82**: Service registration

---

## Summary

The command flow is a layered pipeline with clear separation of concerns:
1. **CommandBus**: Orchestration, persistence, sequencing
2. **CommandDispatcher**: Routing to subscribers
3. **CommandSubscriber**: Saga filtering and invocation
4. **Saga**: Domain logic execution

This design provides:
- **Scalability**: Parallel processing at multiple levels
- **Extensibility**: Interface-based with multiple dispatcher/subscriber support
- **Observability**: Telemetry at each layer
- **Event Sourcing**: Built-in command persistence and replay
- **Cloud Readiness**: Easy to add cloud-based dispatchers/subscribers
