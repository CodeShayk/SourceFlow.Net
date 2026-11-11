# Store Persistence Architecture

## Overview
SourceFlow.Net uses a layered persistence architecture with three distinct store types, each serving a specific purpose in the CQRS and Event Sourcing pattern. This document provides a comprehensive analysis of the storage layer, adapter pattern, and integration with the command/event processing pipeline.

## Three Store Types

### Architectural Separation

```
┌─────────────────────────────────────────────────────────────────┐
│                    SourceFlow Application                        │
└───────────┬─────────────────┬─────────────────┬─────────────────┘
            │                 │                 │
            ▼                 ▼                 ▼
    ┌───────────────┐ ┌───────────────┐ ┌───────────────────┐
    │ ICommandStore │ │ IEntityStore  │ │ IViewModelStore   │
    │  (Commands)   │ │  (Entities)   │ │ (Read Models)     │
    └───────┬───────┘ └───────┬───────┘ └───────┬───────────┘
            │                 │                 │
            │                 │                 │
    ┌───────▼───────┐ ┌───────▼───────┐ ┌───────▼───────────┐
    │ Event Sourcing│ │  Saga State   │ │  CQRS Query Side  │
    │  Command Log  │ │   Storage     │ │   Projections     │
    └───────────────┘ └───────────────┘ └───────────────────┘
```

### 1. ICommandStore - Event Sourcing Log

**Purpose**: Persists all commands for event sourcing and audit trail

**Key Characteristics**:
- **Append-only**: Commands are never modified or deleted
- **Sequenced**: Each command has a sequence number per entity
- **Replayable**: Commands can be replayed to reconstruct state
- **Serialized**: Stores serialized command data (CommandData DTO)

**Interface** (ICommandStore.cs:11-27):
```csharp
public interface ICommandStore
{
    /// <summary>
    /// Appends serialized command data to the store.
    /// </summary>
    Task Append(CommandData commandData);

    /// <summary>
    /// Loads all serialized command data for a given aggregate from the store.
    /// </summary>
    Task<IEnumerable<CommandData>> Load(int aggregateId);
}
```

**When Used**:
- After every command is processed (CommandBus.cs:87-88)
- During replay to reconstruct aggregate state (CommandBus.cs:130)
- For audit trail and compliance

---

### 2. IEntityStore - Saga/Aggregate State

**Purpose**: Persists saga and aggregate entity state

**Key Characteristics**:
- **Mutable**: Entities can be created, updated, and deleted
- **Transactional**: Supports CRUD operations
- **Domain Objects**: Stores domain entities (not serialized DTOs)
- **Scoped**: Wrapped by adapter for per-request lifecycle

**Interface** (IEntityStore.cs:8-31):
```csharp
public interface IEntityStore
{
    /// <summary>
    /// Retrieves an entity by unique identifier.
    /// </summary>
    Task<TEntity> Get<TEntity>(int id) where TEntity : class, IEntity;

    /// <summary>
    /// Creates or updates an entity, persisting its state.
    /// </summary>
    Task<TEntity> Persist<TEntity>(TEntity entity) where TEntity : class, IEntity;

    /// <summary>
    /// Deletes an entity from the store.
    /// </summary>
    Task Delete<TEntity>(TEntity entity) where TEntity : class, IEntity;
}
```

**When Used**:
- Sagas load entity state before command handling
- Sagas persist entity state after command handling
- Aggregates may use for snapshot storage (optimization)

---

### 3. IViewModelStore - CQRS Read Models

**Purpose**: Persists materialized views (read models) for queries

**Key Characteristics**:
- **Denormalized**: Optimized for queries, not normalized
- **Projection**: Built from events via IProjectOn<TEvent>
- **Query Side**: Supports the CQRS query model
- **Eventually Consistent**: Updated asynchronously from events

**Interface** (IViewModelStore.cs:6-30):
```csharp
public interface IViewModelStore
{
    /// <summary>
    /// Retrieves a view model by unique identifier.
    /// </summary>
    Task<TViewModel> Get<TViewModel>(int id) where TViewModel : class, IViewModel;

    /// <summary>
    /// Creates or updates view model, persisting its state.
    /// </summary>
    Task<TViewModel> Persist<TViewModel>(TViewModel model) where TViewModel : class, IViewModel;

    /// <summary>
    /// Deletes a ViewModel (soft or hard delete).
    /// </summary>
    Task Delete<TViewModel>(TViewModel model) where TViewModel : class, IViewModel;
}
```

**When Used**:
- Views project events to update read models (Projections.EventSubscriber.cs:67)
- Application queries read models for data retrieval
- Reports and dashboards query view models

---

## Store Adapter Pattern

### Purpose of Adapters

**Problem**: Stores may need different lifecycle management than consumers

**Solution**: Adapter pattern provides a scoped wrapper around stores

```
Scoped Service (per request)         Singleton/Scoped Service
┌──────────────────────┐             ┌──────────────────────┐
│ ICommandStoreAdapter │────────────>│   ICommandStore      │
└──────────────────────┘             └──────────────────────┘
        │                                      │
        │ - Serialization/Deserialization     │ - Actual persistence
        │ - Telemetry integration             │ - Database operations
        │ - Type conversion                   │ - Transaction management
        │ - Sequence number calculation       │
        └─────────────────────────────────────┘
```

### Adapter Responsibilities

#### 1. ICommandStoreAdapter (CommandStoreAdapter.cs)

**Additional Capabilities Beyond ICommandStore**:

1. **Serialization/Deserialization** (lines 104-182)
   - Converts `ICommand` to `CommandData` (serialize)
   - Converts `CommandData` to `ICommand` (deserialize)
   - Handles type information preservation
   - Uses `ByteArrayPool` for performance

2. **Sequence Number Management** (lines 94-102)
   ```csharp
   public async Task<int> GetNextSequenceNo(int entityId)
   {
       var events = await Load(entityId);
       if (events != null && events.Any())
           return events.Max(c => c.Metadata.SequenceNo) + 1;
       return 1;
   }
   ```

3. **Telemetry Integration** (lines 27-46)
   - Traces append operations
   - Traces load operations
   - Records metrics

4. **Error Handling**
   - Graceful deserialization failures (lines 84-88)
   - Skips commands that can't be deserialized

---

#### 2. IEntityStoreAdapter (EntityStoreAdapter.cs)

**Additional Capabilities**:

1. **Telemetry Wrapping** (lines 17-67)
   - Traces all Get/Persist/Delete operations
   - Tags with entity type and ID
   - Records entity creation metrics

2. **Pass-through Operations**
   - Simple delegation to underlying IEntityStore
   - Adds observability without changing behavior

**Method Signature Differences**:
- `IEntityStore.Get<TEntity>()` → `IEntityStoreAdapter.Get<TEntity>()`
- `IEntityStore.Persist<TEntity>()` → `IEntityStoreAdapter.Persist<TEntity>()`
- No additional methods

---

#### 3. IViewModelStoreAdapter (ViewModelStoreAdapter.cs)

**Additional Capabilities**:

1. **Telemetry Wrapping** (lines 18-67)
   - Traces all Find/Persist/Delete operations
   - Tags with view model type and ID

2. **Method Naming Difference**
   - `IViewModelStore.Get<TViewModel>()` → `IViewModelStoreAdapter.Find<TViewModel>()` (line 18-33)
   - Semantic difference: "Find" suggests search, "Get" suggests direct retrieval

**Pass-through Operations**:
- Simple delegation to underlying IViewModelStore

---

## Command Store Deep Dive

### CommandData DTO

**Purpose**: Serialization-friendly representation of commands

**Structure** (CommandData.cs:8-18):
```csharp
public class CommandData
{
    public int EntityId { get; set; }
    public int SequenceNo { get; set; }
    public string CommandName { get; set; }
    public string CommandType { get; set; }          // AssemblyQualifiedName
    public string PayloadType { get; set; }          // AssemblyQualifiedName
    public string PayloadData { get; set; }          // JSON serialized payload
    public string Metadata { get; set; }             // JSON serialized metadata
    public DateTime Timestamp { get; set; }
}
```

**Why Separate DTO?**
- Commands (ICommand) contain interfaces and complex types
- CommandData is pure data, easy to serialize
- Stores work with data, not domain objects
- Enables polyglot persistence (SQL, NoSQL, etc.)

---

### Serialization Process

**Serialize (ICommand → CommandData)** (CommandStoreAdapter.cs:104-141):

```
ICommand (domain object)
    │
    ├─► Extract Command.Entity.Id → CommandData.EntityId
    ├─► Extract Command.Metadata.SequenceNo → CommandData.SequenceNo
    ├─► Extract Command.Name → CommandData.CommandName
    ├─► Get Command.GetType().AssemblyQualifiedName → CommandData.CommandType
    ├─► Get Command.Payload.GetType().AssemblyQualifiedName → CommandData.PayloadType
    ├─► Serialize Command.Payload to JSON → CommandData.PayloadData
    ├─► Serialize Command.Metadata to JSON → CommandData.Metadata
    └─► Extract Command.Metadata.OccurredOn → CommandData.Timestamp
         │
         ▼
    CommandData (serializable DTO)
```

**Key Implementation Details**:
1. Uses **concrete type** for serialization, not interface (line 110)
2. Uses **ByteArrayPool.Serialize()** for performance (lines 111, 122)
3. Stores **AssemblyQualifiedName** for type reconstruction (lines 119-120)
4. Telemetry trace for serialization performance (lines 127-140)

---

**Deserialize (CommandData → ICommand)** (CommandStoreAdapter.cs:143-182):

```
CommandData (from storage)
    │
    ├─► Deserialize Metadata JSON → Metadata object
    ├─► Type.GetType(CommandData.CommandType) → Get command type
    ├─► Activator.CreateInstance(commandType) → Create command instance
    ├─► Set command.Metadata
    ├─► Set command.Entity = new EntityRef { Id = CommandData.EntityId }
    ├─► Type.GetType(CommandData.PayloadType) → Get payload type
    ├─► ByteArrayPool.Deserialize(PayloadData, payloadType) → Payload object
    └─► Reflection: Set command.Payload property
         │
         ▼
    ICommand (domain object)
```

**Key Implementation Details**:
1. Uses **reflection** to create command instance (line 154)
2. Uses **reflection** to set payload property (lines 173-177)
3. Gracefully handles missing types (returns null, lines 150-151)
4. Handles missing payloads (lines 165-179)

---

### Sequence Number Management

**Purpose**: Ensure ordering of commands per entity

**Algorithm** (CommandStoreAdapter.cs:94-102):
```csharp
public async Task<int> GetNextSequenceNo(int entityId)
{
    var events = await Load(entityId);

    if (events != null && events.Any())
        return events.Max(c => c.Metadata.SequenceNo) + 1;

    return 1; // First command for this entity
}
```

**Called From**: CommandBus.cs:74
```csharp
if (!command.Metadata.IsReplay)
    command.Metadata.SequenceNo = await commandStore.GetNextSequenceNo(command.Entity.Id);
```

**Important Notes**:
- Only called for NEW commands (not replays)
- Loads all commands to find max sequence number
- Potential optimization: Store max sequence separately
- Ensures commands can be replayed in order

---

### Integration with Command Flow

**Command Persistence Flow**:

```
1. CommandBus receives command
    │
2. Get next sequence number
    │   commandStore.GetNextSequenceNo(entityId)
    │
3. Set command.Metadata.SequenceNo
    │
4. Dispatch to all ICommandDispatcher instances
    │   (Sagas process command)
    │
5. Append command to store (if not replay)
    │   commandStore.Append(command)
    │       │
    │       ├─► Serialize command → CommandData
    │       └─► store.Append(commandData)
    │
6. CommandData persisted to storage
```

**Replay Flow**:

```
1. CommandBus.Replay(entityId) called
    │
2. Load all commands for entity
    │   commandStore.Load(entityId)
    │       │
    │       ├─► store.Load(entityId) → CommandData[]
    │       └─► Deserialize CommandData[] → ICommand[]
    │
3. For each command:
    │   Set command.Metadata.IsReplay = true
    │   Dispatch(command)
    │       │
    │       ├─► Sequence number NOT recalculated (already set)
    │       ├─► Sagas process command
    │       └─► Command NOT re-appended to store
    │
4. Entity state reconstructed
```

---

## Entity Framework Implementation Example

### EfCommandStore (Concrete Implementation)

**File**: `SourceFlow.Net.EntityFramework/Stores/EfCommandStore.cs`

**Key Features**:

1. **Database Context** (line 14)
   ```csharp
   private readonly CommandDbContext _context;
   ```

2. **Resilience Policy** (line 37)
   ```csharp
   await _resiliencePolicy.ExecuteAsync(async () =>
   {
       // Database operation with retry logic
   });
   ```

3. **Telemetry Integration** (lines 33-67)
   ```csharp
   await _telemetryService.TraceAsync(
       "sourceflow.ef.command.append",
       async () => { /* operation */ },
       activity => { /* tags */ }
   );
   ```

4. **Entity Mapping** (lines 39-51)
   - Maps `CommandData` to `CommandRecord` (EF entity)
   - Adds audit fields: `CreatedAt`, `UpdatedAt`

5. **Change Tracker Management** (line 57)
   ```csharp
   _context.ChangeTracker.Clear();
   ```
   - Prevents caching issues
   - Important for high-throughput scenarios

6. **Ordered Retrieval** (lines 78-82)
   ```csharp
   var commandRecords = await _context.Commands
       .AsNoTracking()
       .Where(c => c.EntityId == entityId)
       .OrderBy(c => c.SequenceNo)  // Critical for replay
       .ToListAsync();
   ```

---

### CommandRecord Entity Model

**File**: `SourceFlow.Net.EntityFramework/Models/CommandRecord.cs`

**Schema** (lines 8-27):
```csharp
public class CommandRecord
{
    public long Id { get; set; }                    // Auto-increment primary key
    public int EntityId { get; set; }               // Aggregate ID
    public int SequenceNo { get; set; }             // Order within aggregate
    public string CommandName { get; set; }         // Human-readable name
    public string CommandType { get; set; }         // Fully qualified type name
    public string PayloadType { get; set; }         // Payload type name
    public string PayloadData { get; set; }         // JSON payload
    public string Metadata { get; set; }            // JSON metadata
    public DateTime Timestamp { get; set; }         // Command timestamp
    public DateTime CreatedAt { get; set; }         // Audit: when stored
    public DateTime UpdatedAt { get; set; }         // Audit: when modified
}
```

**Indexes** (lines 29-37):
```csharp
builder.HasKey(c => c.Id);
builder.HasIndex(c => new { c.EntityId, c.SequenceNo }).IsUnique();  // Unique per entity
builder.HasIndex(c => c.EntityId);                                   // Query by entity
builder.HasIndex(c => c.Timestamp);                                  // Query by time
```

**Index Purposes**:
1. `(EntityId, SequenceNo)` UNIQUE: Ensures no duplicate sequences per entity
2. `EntityId`: Fast loading of all commands for an entity
3. `Timestamp`: Time-based queries for analytics

---

## Service Lifetime Management

### Why Different Lifetimes?

| Service | Lifetime | Reason |
|---------|----------|--------|
| **ICommandStore** | Singleton/Scoped | Implementation-dependent (EF needs scoped) |
| **IEntityStore** | Singleton/Scoped | Implementation-dependent |
| **IViewModelStore** | Singleton/Scoped | Implementation-dependent |
| **ICommandStoreAdapter** | **Scoped** | Per-request serialization state |
| **IEntityStoreAdapter** | **Scoped** | Per-request entity tracking |
| **IViewModelStoreAdapter** | **Scoped** | Per-request projection state |

### Registration (IocExtensions.cs:56-75)

```csharp
// Register stores (Singleton by default, but can be overridden)
services.AddFirstImplementationAsInterface<IEntityStore>(assemblies, lifetime);
services.AddFirstImplementationAsInterface<IViewModelStore>(assemblies, lifetime);
services.AddFirstImplementationAsInterface<ICommandStore>(assemblies, lifetime);

// Register adapters (ALWAYS Scoped)
services.TryAddScoped<IEntityStoreAdapter, EntityStoreAdapter>();
services.TryAddScoped<IViewModelStoreAdapter, ViewModelStoreAdapter>();
services.TryAddScoped<ICommandStoreAdapter, CommandStoreAdapter>();
```

**Why Adapters are Scoped**:
1. **Isolation**: Each request gets own adapter instance
2. **Thread Safety**: No shared state between concurrent requests
3. **Transaction Support**: EF DbContext is scoped
4. **Memory Management**: Released after request completes

---

## Performance Optimizations

### 1. ByteArrayPool for Serialization

**Location**: CommandStoreAdapter.cs:111, 122

**Pattern**:
```csharp
var payloadJson = ByteArrayPool.Serialize(command.Payload, command.Payload.GetType());
```

**Benefits**:
- Reuses byte arrays from ArrayPool
- Reduces GC pressure
- Faster than creating new byte[] each time

---

### 2. AsNoTracking for Reads

**Location**: EfCommandStore.cs:79

**Pattern**:
```csharp
var commandRecords = await _context.Commands
    .AsNoTracking()  // Don't track for read-only operations
    .Where(c => c.EntityId == entityId)
    .ToListAsync();
```

**Benefits**:
- Faster queries (no change tracking overhead)
- Lower memory usage
- Appropriate for read-only replay scenarios

---

### 3. Change Tracker Clear

**Location**: EfCommandStore.cs:57

**Pattern**:
```csharp
await _context.SaveChangesAsync();
_context.ChangeTracker.Clear();  // Clear after save
```

**Benefits**:
- Prevents memory leaks in long-running processes
- Avoids stale entity issues
- Important for high-throughput command appending

---

### 4. Batch Loading

**Pattern**: Load all commands for entity in single query

```csharp
var commandRecords = await _context.Commands
    .Where(c => c.EntityId == entityId)
    .OrderBy(c => c.SequenceNo)
    .ToListAsync();  // Single DB round-trip
```

**Benefits**:
- Avoids N+1 query problem
- Reduces database round-trips
- Faster replay performance

---

## Cloud Storage Considerations

### Distributed Storage for AWS Extension

When extending to cloud, consider storage distribution:

```
┌─────────────────────────────────────────────────────────────┐
│                     Application Instance 1                   │
└───┬────────────────┬────────────────┬───────────────────────┘
    │                │                │
    │                │                │
┌───▼────────┐ ┌─────▼──────┐ ┌──────▼───────┐
│ ICommand   │ │ IEntity    │ │ IViewModel   │
│ Store      │ │ Store      │ │ Store        │
└───┬────────┘ └─────┬──────┘ └──────┬───────┘
    │                │                │
    │  Shared        │  Shared        │  Shared
    ▼  Storage       ▼  Storage       ▼  Storage
┌────────────────────────────────────────────────┐
│         Centralized Database (RDS)             │
│   or Document Store (DynamoDB, DocumentDB)     │
└────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│                     Application Instance 2                   │
│  (Processing messages from SQS/SNS)                          │
└───┬────────────────┬────────────────┬───────────────────────┘
    │                │                │
    └────────────────┴────────────────┴───> Same Shared Storage
```

---

### Storage Options for Cloud

#### 1. Relational Databases (Amazon RDS)

**Best For**: ICommandStore, IEntityStore

**Pros**:
- ACID transactions
- Indexing for fast queries
- Proven EF Core support
- Strong consistency

**Cons**:
- Vertical scaling limits
- Higher latency than NoSQL

**Example**: PostgreSQL on RDS
```csharp
services.AddDbContext<CommandDbContext>(options =>
    options.UseNpgsql(connectionString));
```

---

#### 2. Document Databases (Amazon DynamoDB)

**Best For**: IViewModelStore (read models)

**Pros**:
- Horizontal scaling
- Low latency
- Flexible schema
- Global tables for multi-region

**Cons**:
- Eventually consistent (by default)
- No complex queries
- No joins

**Example**: DynamoDB for view models
```csharp
public class DynamoDbViewModelStore : IViewModelStore
{
    private readonly IAmazonDynamoDB _dynamoDb;

    public async Task<TViewModel> Get<TViewModel>(int id)
    {
        var request = new GetItemRequest
        {
            TableName = typeof(TViewModel).Name,
            Key = new Dictionary<string, AttributeValue>
            {
                ["Id"] = new AttributeValue { N = id.ToString() }
            }
        };

        var response = await _dynamoDb.GetItemAsync(request);
        // Deserialize from DynamoDB attributes
    }
}
```

---

#### 3. Event Store Databases (Amazon EventBridge Archive, DynamoDB Streams)

**Best For**: ICommandStore (event sourcing log)

**Pros**:
- Purpose-built for event sourcing
- Append-only optimization
- Stream processing support
- Automatic archiving

**Example**: DynamoDB for command log
```csharp
public class DynamoDbCommandStore : ICommandStore
{
    public async Task Append(CommandData commandData)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["EntityId"] = new AttributeValue { N = commandData.EntityId.ToString() },
            ["SequenceNo"] = new AttributeValue { N = commandData.SequenceNo.ToString() },
            ["CommandType"] = new AttributeValue { S = commandData.CommandType },
            ["PayloadData"] = new AttributeValue { S = commandData.PayloadData },
            // ... other fields
        };

        await _dynamoDb.PutItemAsync(new PutItemRequest
        {
            TableName = "CommandLog",
            Item = item,
            ConditionExpression = "attribute_not_exists(EntityId) AND attribute_not_exists(SequenceNo)"
            // Ensures uniqueness
        });
    }
}
```

---

### Multi-Region Considerations

For global applications:

1. **Command Store**: Replicate to multiple regions for disaster recovery
2. **Entity Store**: Active-active with conflict resolution OR active-passive with failover
3. **ViewModel Store**: Eventually consistent cross-region replication

**AWS Services**:
- RDS Cross-Region Replicas
- DynamoDB Global Tables
- Aurora Global Database

---

## Storage Migration Strategy

### Phased Migration to Cloud Storage

#### Phase 1: Shared Database
```
[Local App] ──┐
              ├──> [RDS PostgreSQL] (shared)
[Cloud App] ──┘
```
- Both local and cloud apps use same database
- Simplest approach for initial cloud extension
- No data consistency issues

#### Phase 2: Read Replica for Views
```
[Local App] ──> [RDS Primary] ──> [RDS Read Replica]
                                        │
[Cloud App] ───────────────────────────┘
                   (reads only)
```
- Command/Entity writes go to primary
- View reads can use read replica
- Reduces load on primary

#### Phase 3: Polyglot Persistence
```
[Local App] ──┬──> [RDS PostgreSQL] (Commands, Entities)
              │
              └──> [DynamoDB] (ViewModels)
                       │
[Cloud App] ───────────┴──> [DynamoDB] (ViewModels)
```
- Command/Entity stores remain in RDS
- ViewModel store migrated to DynamoDB
- Better scalability for read models

---

## Best Practices

### 1. Command Store Best Practices

✅ **DO**:
- Keep command store append-only
- Index on (EntityId, SequenceNo) for replay
- Archive old commands periodically
- Use immutable storage for compliance

❌ **DON'T**:
- Delete commands (breaks replay)
- Modify commands after append
- Store large binary payloads inline (use references)
- Skip sequence numbers

---

### 2. Entity Store Best Practices

✅ **DO**:
- Use transactions for consistency
- Implement optimistic concurrency
- Cache frequently accessed entities
- Consider snapshot pattern for large aggregates

❌ **DON'T**:
- Share entity instances across scopes
- Hold entities in memory indefinitely
- Ignore concurrency conflicts
- Store event-sourced aggregates here (reconstruct from commands)

---

### 3. ViewModel Store Best Practices

✅ **DO**:
- Denormalize for query performance
- Use separate table per view model type
- Index on query patterns
- Accept eventual consistency
- Implement rebuild capability

❌ **DON'T**:
- Normalize (defeats purpose of read models)
- Use as source of truth (events are source)
- Rely on strong consistency
- Forget to handle duplicate events (idempotency)

---

### 4. Adapter Best Practices

✅ **DO**:
- Keep adapters stateless
- Use telemetry for observability
- Handle serialization errors gracefully
- Validate data before persistence

❌ **DON'T**:
- Cache data in adapters
- Implement business logic in adapters
- Reuse adapter instances across scopes
- Ignore deserialization failures

---

## Storage Extension Points Summary

| Extension Point | Interface | Purpose | Example Implementation |
|----------------|-----------|---------|------------------------|
| **Command Storage** | `ICommandStore` | Event sourcing log | EfCommandStore, DynamoDbCommandStore |
| **Entity Storage** | `IEntityStore` | Saga/Aggregate state | EfEntityStore, CosmosDbEntityStore |
| **ViewModel Storage** | `IViewModelStore` | Read models | EfViewModelStore, DynamoDbViewModelStore |

**How to Extend**:
1. Implement the interface (ICommandStore, IEntityStore, or IViewModelStore)
2. Register implementation in IoC container
3. Adapters automatically wrap your implementation
4. No changes to core SourceFlow code needed

**Example Registration**:
```csharp
// Default registration (first implementation wins)
services.UseSourceFlow(assemblies);

// OR override with custom implementation
services.AddSingleton<ICommandStore, MyCustomCommandStore>();
services.AddScoped<IEntityStore, MyCustomEntityStore>();
services.AddScoped<IViewModelStore, MyCustomViewModelStore>();
```

---

## Integration with AWS Cloud Extension

### Storage in Cloud Scenarios

When using AWS SQS/SNS dispatchers:

1. **Command Store**:
   - Still appends after local processing
   - Cloud listener processes from SQS, but doesn't re-append
   - Single source of truth

2. **Entity Store**:
   - Must be accessible by both local and cloud instances
   - Use centralized database (RDS, Aurora)
   - Consider read replicas for scaling

3. **ViewModel Store**:
   - Can be distributed (DynamoDB Global Tables)
   - Each region maintains own read models
   - Eventually consistent across regions

### Recommended Architecture

```
┌──────────────────┐           ┌──────────────────┐
│   Local App      │           │   Cloud App      │
│                  │           │  (SQS Listener)  │
└────┬─────┬───────┘           └─────┬─────┬──────┘
     │     │                         │     │
     │     │    ┌────────────────────┘     │
     │     │    │                          │
     │     │    │        ┌─────────────────┘
     │     │    │        │
     ▼     ▼    ▼        ▼
┌─────────────────────────────┐
│   Amazon RDS PostgreSQL     │
│   - CommandStore            │
│   - EntityStore             │
└─────────────────────────────┘

     │                │
     ▼                ▼
┌─────────────────────────────┐
│   Amazon DynamoDB           │
│   - ViewModelStore          │
│   (Global Tables)           │
└─────────────────────────────┘
```

---

## Summary

The SourceFlow.Net storage layer provides:

1. **Separation**: Three distinct stores for different purposes
2. **Flexibility**: Interface-based, easy to swap implementations
3. **Performance**: Optimizations like ByteArrayPool, AsNoTracking
4. **Observability**: Built-in telemetry and tracing
5. **Scalability**: Adapter pattern supports scoped lifecycles
6. **Cloud-Ready**: Extensible to distributed storage systems

**Key Takeaways**:
- **CommandStore** = Event sourcing log (append-only, sequenced)
- **EntityStore** = Saga state (mutable, transactional)
- **ViewModelStore** = Read models (denormalized, eventually consistent)
- **Adapters** = Add telemetry, serialization, lifecycle management
- **Cloud Extension** = Use shared database or distributed storage (DynamoDB, RDS)
