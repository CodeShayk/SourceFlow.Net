# SourceFlow.Net.EntityFramework

Entity Framework Core persistence provider for SourceFlow.Net. Provides implementations of `ICommandStore`, `IEntityStore`, and `IViewModelStore` using Entity Framework Core with full support for relational data models.

## Features

- **Entity Framework Core implementation** of SourceFlow stores
- **Clean separation of concerns**: persistence layer handles only database operations
- **CommandData DTO** for serialization separation from domain logic
- **SQL Server by default**: Convenient methods for SQL Server with single or separate connection strings
- **Database-agnostic support**: Use PostgreSQL, MySQL, SQLite, or any EF Core provider
- **Flexible table naming conventions**: Configure casing, pluralization, prefixes, suffixes, and schemas
- **Full async support** with proper Entity Framework tracking management
- **Scoped service lifetimes** to prevent captive dependency issues
- **Optimized change tracking** with `AsNoTracking()` and entity detachment
- **Mix and match databases**: Use different databases for commands, entities, and view models

## Architecture

### Layered Design

The implementation follows a clean layered architecture:

1. **Store Layer** (`EfCommandStore`, `EfEntityStore`, `EfViewModelStore`)
   - Handles only database persistence operations
   - Works with data transfer objects (DTOs) for commands
   - Uses Entity Framework Core for data access
   - Manages change tracking and database connections

2. **Adapter Layer** (`CommandStoreAdapter`, `EntityStoreAdapter`, `ViewModelStoreAdapter`)
   - Handles serialization/deserialization of domain objects
   - Converts between domain models and DTOs
   - Lives in the core `SourceFlow` package

3. **Service Lifetimes**
   - All stores and adapters are registered as **Scoped** services
   - Prevents captive dependency issues with DbContext
   - Ensures proper disposal of database connections

### CommandData DTO

The `CommandData` class is a data transfer object used for command persistence:

```csharp
public class CommandData
{
    public int EntityId { get; set; }
    public int SequenceNo { get; set; }
    public string CommandName { get; set; }
    public string CommandType { get; set; }
    public string PayloadType { get; set; }
    public string PayloadData { get; set; }
    public string Metadata { get; set; }
    public DateTime Timestamp { get; set; }
}
```

This separation ensures:
- `ICommandStore` interface works with serialized data only
- Serialization logic lives in `CommandStoreAdapter`
- Database layer is independent of domain serialization concerns
- Better testability and maintainability

### Change Tracking Optimization

The stores use several techniques to optimize Entity Framework change tracking:

- `AsNoTracking()` for read operations to improve performance
- `EntityState.Detached` after save operations to prevent tracking conflicts
- `ChangeTracker.Clear()` in command store to prevent caching issues
- Ensures concurrent operations don't conflict with tracked entities

## Installation

```xml
<PackageReference Include="SourceFlow.Stores.EntityFramework" Version="1.0.0" />
```

## Usage

SourceFlow.Net.EntityFramework provides two types of registration methods:

1. **SQL Server Methods** - Convenient methods that use SQL Server by default (`AddSourceFlowEfStores`)
2. **Database-Agnostic Methods** - Use any EF Core provider (`AddSourceFlowEfStoresWithCustomProvider`)

### SQL Server (Default Provider)

#### Single Connection String

```csharp
services.AddSourceFlowEfStores("Server=localhost;Database=SourceFlow;Trusted_Connection=true;");
```

#### Separate Connection Strings Per Store

```csharp
services.AddSourceFlowEfStores(
    commandConnectionString: "Server=localhost;Database=SourceFlow.Commands;Trusted_Connection=true;",
    entityConnectionString: "Server=localhost;Database=SourceFlow.Entities;Trusted_Connection=true;",
    viewModelConnectionString: "Server=localhost;Database=SourceFlow.ViewModels;Trusted_Connection=true;"
);
```

### Other Databases (Custom Provider)

For PostgreSQL, MySQL, SQLite, or any other EF Core supported database:

#### PostgreSQL

```csharp
services.AddSourceFlowEfStoresWithCustomProvider(options =>
    options.UseNpgsql("Host=localhost;Database=sourceflow;Username=postgres;Password=pass"));
```

#### MySQL

```csharp
var serverVersion = new MySqlServerVersion(new Version(8, 0, 21));
services.AddSourceFlowEfStoresWithCustomProvider(options =>
    options.UseMySql("Server=localhost;Database=sourceflow;User=root;Password=pass", serverVersion));
```

#### SQLite

```csharp
services.AddSourceFlowEfStoresWithCustomProvider(options =>
    options.UseSqlite("Data Source=sourceflow.db"));
```

#### Different Databases Per Store

You can even use different databases for each store:

```csharp
services.AddSourceFlowEfStoresWithCustomProviders(
    commandContextConfig: opt => opt.UseNpgsql(postgresConnectionString),
    entityContextConfig: opt => opt.UseSqlite(sqliteConnectionString),
    viewModelContextConfig: opt => opt.UseSqlServer(sqlServerConnectionString)
);
```

### Using Configuration (SQL Server)

You can also configure connection strings using `IConfiguration`:

```csharp
// In appsettings.json:
{
  "ConnectionStrings": {
    "SourceFlow.Command": "Server=localhost;Database=SourceFlow.Commands;Trusted_Connection=true;",
    "SourceFlow.Entity": "Server=localhost;Database=SourceFlow.Entities;Trusted_Connection=true;",
    "SourceFlow.ViewModel": "Server=localhost;Database=SourceFlow.ViewModels;Trusted_Connection=true;"
  }
}

// In your startup code:
services.AddSourceFlowEfStores(configuration);
```

### Options-Based Configuration

For more complex scenarios with SQL Server, you can use the options pattern:

```csharp
services.AddSourceFlowEfStores(options =>
{
    options.CommandConnectionString = GetCommandConnectionString();
    options.EntityConnectionString = GetEntityConnectionString();
    options.ViewModelConnectionString = GetViewModelConnectionString();
});
```

## Connection String Resolution (SQL Server methods)

The system follows this hierarchy for connection string resolution:

1. If a specific connection string is configured for a store type, use it
2. If no specific string exists, fall back to the default connection string
3. If neither is available, throw an exception

## Supported Databases

This package works with any database that Entity Framework Core supports through the generic configuration methods, including:

- SQL Server (via dedicated methods)
- SQLite (via generic methods)
- PostgreSQL (via generic methods)
- MySQL (via generic methods)
- Oracle (via generic methods)
- In-memory databases (via generic methods)
- And many others

## Testing

For testing scenarios, we recommend using SQLite in-memory databases:

```csharp
services.AddSourceFlowEfStoresWithCustomProvider(optionsBuilder =>
    optionsBuilder.UseSqlite("DataSource=:memory:"));
```

Or for SQL Server testing:

```csharp
services.AddSourceFlowEfStores("DataSource=:memory:");
```

## Table Naming Conventions

SourceFlow.Net.EntityFramework supports flexible table naming conventions to match your database standards.

### Configuration

Configure naming conventions using the `SourceFlowEfOptions`:

```csharp
services.AddSourceFlowEfStores(options =>
{
    options.DefaultConnectionString = connectionString;

    // Configure entity table naming
    options.EntityTableNaming.Casing = TableNameCasing.SnakeCase;
    options.EntityTableNaming.Pluralize = true;
    options.EntityTableNaming.Prefix = "sf_";

    // Configure view model table naming
    options.ViewModelTableNaming.Casing = TableNameCasing.PascalCase;
    options.ViewModelTableNaming.Suffix = "View";

    // Configure command table naming
    options.CommandTableNaming.Casing = TableNameCasing.LowerCase;
    options.CommandTableNaming.UseSchema = true;
    options.CommandTableNaming.SchemaName = "audit";
});
```

### Naming Convention Options

Each `TableNamingConvention` supports the following options:

**Casing Styles:**
- `PascalCase` - First letter capitalized (e.g., `BankAccount`)
- `CamelCase` - First letter lowercase (e.g., `bankAccount`)
- `SnakeCase` - Lowercase with underscores (e.g., `bank_account`)
- `LowerCase` - All lowercase (e.g., `bankaccount`)
- `UpperCase` - All uppercase (e.g., `BANKACCOUNT`)

**Other Options:**
- `Pluralize` - Pluralize table names (e.g., `BankAccount` → `BankAccounts`)
- `Prefix` - Add a prefix to all table names (e.g., `"sf_"` → `sf_BankAccount`)
- `Suffix` - Add a suffix to all table names (e.g., `"_tbl"` → `BankAccount_tbl`)
- `UseSchema` - Whether to use a schema name
- `SchemaName` - The schema name to use (when `UseSchema` is true)

### Examples

**Snake case with pluralization:**
```csharp
options.EntityTableNaming.Casing = TableNameCasing.SnakeCase;
options.EntityTableNaming.Pluralize = true;
// BankAccount → bank_accounts
```

**Prefix for all entity tables:**
```csharp
options.EntityTableNaming.Prefix = "Entity_";
// BankAccount → Entity_BankAccount
```

**Schema-based organization:**
```csharp
options.CommandTableNaming.UseSchema = true;
options.CommandTableNaming.SchemaName = "commands";
// Commands table goes in commands.CommandRecord schema
```

**Combined conventions:**
```csharp
options.ViewModelTableNaming.Casing = TableNameCasing.SnakeCase;
options.ViewModelTableNaming.Prefix = "vm_";
options.ViewModelTableNaming.Pluralize = true;
// AccountSummary → vm_account_summaries
```

### Default Behavior

By default, all naming conventions use:
- `Casing = TableNameCasing.PascalCase`
- `Pluralize = false`
- No prefix or suffix
- No schema

If you don't configure naming conventions, tables will be named using the entity/view model type names as-is.

## Implementation Details

### Command Serialization

Commands are serialized using `System.Text.Json` with the following approach:

- **Payload serialization**: Uses the concrete type of the payload, not the interface type
- **Type information**: Stores `AssemblyQualifiedName` for both command and payload types
- **Metadata**: Serialized separately to maintain sequence numbers and timestamps
- **Deserialization**: Uses reflection to recreate command instances with parameterless constructors

Example from `CommandStoreAdapter`:

```csharp
// Serialize using concrete type to capture all properties
var payloadJson = command.Payload != null
    ? System.Text.Json.JsonSerializer.Serialize(command.Payload, command.Payload.GetType())
    : string.Empty;
```

### Entity Framework Tracking Management

The stores implement careful tracking management to prevent common EF Core issues:

**In EfCommandStore:**
```csharp
// Clear change tracker after save to prevent caching issues
_context.Commands.Add(commandRecord);
await _context.SaveChangesAsync();
_context.ChangeTracker.Clear();
```

**In EfEntityStore and EfViewModelStore:**
```csharp
// Use AsNoTracking for existence checks
var exists = await _context.Set<TEntity>()
    .AsNoTracking()
    .AnyAsync(e => e.Id == entity.Id);

// Detach after save to prevent tracking conflicts
await _context.SaveChangesAsync();
_context.Entry(entity).State = EntityState.Detached;
```

### Service Registration

All services are registered with **Scoped** lifetime:

```csharp
// Store adapters must be Scoped to match the lifetime of the underlying stores
services.TryAddScoped<IEntityStoreAdapter, EntityStoreAdapter>();
services.TryAddScoped<IViewModelStoreAdapter, ViewModelStoreAdapter>();
services.TryAddScoped<ICommandStoreAdapter, CommandStoreAdapter>();

// Stores are also Scoped to work with DbContext
services.AddScoped<ICommandStore, EfCommandStore>();
services.AddScoped<IEntityStore, EfEntityStore>();
services.AddScoped<IViewModelStore, EfViewModelStore>();
```

This prevents the captive dependency anti-pattern where singleton services capture scoped DbContext instances.

## Best Practices

1. **Always use Scoped services** - Don't register stores or adapters as Singleton
2. **Enable database migrations** - Call `ApplyMigrations()` on EntityDbContext and ViewModelDbContext after `EnsureCreated()`
3. **Handle deserialization failures** - The CommandStoreAdapter silently skips commands that can't be deserialized
4. **Use parameterless constructors** - All command classes need a parameterless constructor for deserialization
5. **Separate databases for testing** - Use fresh database instances for each test to avoid state conflicts
6. **Configure proper connection pooling** - For production, ensure your connection strings include appropriate pooling settings

## Troubleshooting

### "Instance already being tracked" errors
This occurs when Entity Framework tries to track multiple instances of the same entity. The stores now use `AsNoTracking()` and entity detachment to prevent this.

### Commands not deserializing correctly
Ensure your command classes have:
- A public parameterless constructor
- The payload is serialized using the concrete type, not the interface

### Sequence number conflicts
The `CommandStoreAdapter` calculates the next sequence number by loading all commands for an entity. Ensure concurrent operations are properly serialized at the application level if needed.

### DbContext lifetime issues
All stores and adapters must be registered as Scoped services. Singleton registration will cause DbContext lifetime issues and connection leaks.
