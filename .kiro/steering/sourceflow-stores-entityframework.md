# SourceFlow Entity Framework Stores

**Project**: `src/SourceFlow.Stores.EntityFramework/`  
**Purpose**: Entity Framework Core persistence implementations for SourceFlow stores

## Core Functionality

### Store Implementations
- **`EfCommandStore`** - Event sourcing log using `CommandRecord` model
- **`EfEntityStore`** - Saga/aggregate state persistence with generic entity support
- **`EfViewModelStore`** - Read model persistence with optimized queries

### DbContext Architecture
- **`CommandDbContext`** - Commands table with sequence ordering
- **`EntityDbContext`** - Generic entity storage with JSON serialization
- **`ViewModelDbContext`** - View model tables with configurable naming

## Configuration Options

### Connection String Patterns
```csharp
// Single connection string for all stores
services.AddSourceFlowEfStores(connectionString);

// Separate connection strings per store type
services.AddSourceFlowEfStores(commandConn, entityConn, viewModelConn);

// Configuration-based setup
services.AddSourceFlowEfStores(configuration);

// Options-based configuration
services.AddSourceFlowEfStores(options => {
    options.DefaultConnectionString = connectionString;
    options.CommandTableNaming = TableNamingConvention.Singular;
});
```

### Database Provider Support
- **SQL Server** - Default provider for all `AddSourceFlowEfStores` methods
- **Custom Providers** - Use `AddSourceFlowEfStoresWithCustomProvider` for PostgreSQL, MySQL, SQLite
- **Mixed Providers** - Use `AddSourceFlowEfStoresWithCustomProviders` for different databases per store

## Key Features

### Resilience & Reliability
- **Polly Integration** - `IDatabaseResiliencePolicy` with retry policies
- **Circuit Breaker** - Fault tolerance for database operations
- **Transaction Management** - Proper EF Core transaction handling

### Observability
- **OpenTelemetry** - Database operation tracing and metrics
- **`IDatabaseTelemetryService`** - Custom metrics for store operations
- **Performance Counters** - Command appends, entity loads, view updates

### Table Naming Conventions
- **`TableNamingConvention`** - Singular, Plural, or Custom naming
- **Per-Store Configuration** - Different naming per store type
- **Runtime Configuration** - Set via `SourceFlowEfOptions`

## Service Registration

### Core Pattern
```csharp
services.AddSourceFlowEfStores(connectionString);
// Automatically registers:
// - ICommandStore -> EfCommandStore
// - IEntityStore -> EfEntityStore  
// - IViewModelStore -> EfViewModelStore
// - DbContexts with proper lifetimes
// - Resilience and telemetry services
```

### Service Lifetimes
- **Scoped**: All stores, DbContexts, resilience policies (transaction boundaries)
- **Singleton**: Configuration options, telemetry services

## Database Schema

### CommandRecord Model
```csharp
public class CommandRecord
{
    public int Id { get; set; }           // Primary key
    public int EntityId { get; set; }     // Entity reference
    public int SequenceNo { get; set; }   // Ordering within entity
    public string CommandName { get; set; }
    public string CommandType { get; set; }
    public string PayloadType { get; set; }
    public string PayloadData { get; set; } // JSON
    public string Metadata { get; set; }    // JSON
    public DateTime Timestamp { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

### Entity Storage
- Generic `TEntity` serialization to JSON
- Configurable table names per entity type
- Optimistic concurrency with timestamps

### View Model Storage
- Strongly-typed view model tables
- Denormalized for query optimization
- `AsNoTracking()` for read-only operations

## Migration Support

### `DbContextMigrationHelper`
- Automated migration execution
- Database creation and seeding
- Environment-specific migration strategies

## Performance Optimizations

### Query Patterns
- `AsNoTracking()` for read-only operations
- Indexed queries on EntityId and SequenceNo
- Bulk operations for large datasets

### Memory Management
- Change tracker clearing after operations
- Minimal object allocation patterns
- Connection pooling support

## Configuration Examples

### PostgreSQL Setup
```csharp
services.AddSourceFlowEfStoresWithCustomProvider(options =>
    options.UseNpgsql(connectionString));
```

### Mixed Database Setup
```csharp
services.AddSourceFlowEfStoresWithCustomProviders(
    commandConfig: opt => opt.UseNpgsql(postgresConn),
    entityConfig: opt => opt.UseSqlite(sqliteConn),
    viewModelConfig: opt => opt.UseSqlServer(sqlServerConn));
```

## Development Guidelines
- Use `IDatabaseResiliencePolicy` for all database operations
- Implement proper error handling and logging
- Configure appropriate connection strings per environment
- Use migrations for schema changes
- Monitor performance with telemetry services
- Consider read replicas for view model queries