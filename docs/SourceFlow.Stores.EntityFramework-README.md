# SourceFlow.Stores.EntityFramework

Entity Framework Core persistence provider for SourceFlow.Net with support for SQL Server and configurable connection strings per store type.

## Features

- **Complete Store Implementations**: ICommandStore, IEntityStore, and IViewModelStore
- **Flexible Configuration**: Separate or shared connection strings per store type
- **SQL Server Support**: Built-in SQL Server database provider
- **Resilience Policies**: Polly-based retry and circuit breaker patterns
- **Observability**: OpenTelemetry instrumentation for database operations
- **Multi-Framework Support**: .NET 8.0, .NET 9.0, .NET 10.0

## Installation

```bash
# Install the core package
dotnet add package SourceFlow.Net

# Install the Entity Framework provider
dotnet add package SourceFlow.Stores.EntityFramework
```

## Quick Start

### 1. Configure Connection Strings

Add connection strings to your `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "CommandStore": "Server=localhost;Database=SourceFlowCommands;Trusted_Connection=True;",
    "EntityStore": "Server=localhost;Database=SourceFlowEntities;Trusted_Connection=True;",
    "ViewModelStore": "Server=localhost;Database=SourceFlowViews;Trusted_Connection=True;"
  }
}
```

Or use a single shared connection string:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost;Database=SourceFlow;Trusted_Connection=True;"
  }
}
```

### 2. Register Services

```csharp
services.AddSourceFlowStores(configuration, options =>
{
    // Use separate databases for each store
    options.UseCommandStore("CommandStore");
    options.UseEntityStore("EntityStore");
    options.UseViewModelStore("ViewModelStore");

    // Or use a single shared database
    // options.UseSharedConnectionString("DefaultConnection");
});
```

### 3. Apply Migrations

The provider automatically creates the necessary database schema when you run your application. For production scenarios, generate and apply migrations:

```bash
dotnet ef migrations add InitialCreate --context CommandStoreContext
dotnet ef database update --context CommandStoreContext
```

## Configuration Options

### Separate Databases

Configure different databases for commands, entities, and view models:

```csharp
services.AddSourceFlowStores(configuration, options =>
{
    options.UseCommandStore("CommandStoreConnection");
    options.UseEntityStore("EntityStoreConnection");
    options.UseViewModelStore("ViewModelStoreConnection");
});
```

### Shared Database

Use a single database for all stores:

```csharp
services.AddSourceFlowStores(configuration, options =>
{
    options.UseSharedConnectionString("DefaultConnection");
});
```

### Custom DbContext Options

Apply additional EF Core configuration:

```csharp
services.AddSourceFlowStores(configuration, options =>
{
    options.UseCommandStore("CommandStore", dbOptions =>
    {
        dbOptions.EnableSensitiveDataLogging();
        dbOptions.EnableDetailedErrors();
    });
});
```

## Resilience

The provider includes built-in Polly resilience policies for:
- Transient error retry with exponential backoff
- Circuit breaker for database failures
- Automatic reconnection handling

## Documentation

- [Full Documentation](https://github.com/CodeShayk/SourceFlow.Net/wiki)
- [GitHub Repository](https://github.com/CodeShayk/SourceFlow.Net)
- [Report Issues](https://github.com/CodeShayk/SourceFlow.Net/issues)
- [Release Notes](https://github.com/CodeShayk/SourceFlow.Net/blob/v1.0.0/CHANGELOG.md)

## Support

- **Issues**: [GitHub Issues](https://github.com/CodeShayk/SourceFlow.Net/issues/new/choose)
- **Discussions**: [GitHub Discussions](https://github.com/CodeShayk/SourceFlow.Net/discussions)

## License

This project is licensed under the [MIT License](https://github.com/CodeShayk/SourceFlow.Net/blob/master/LICENSE).
