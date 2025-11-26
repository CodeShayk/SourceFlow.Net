# SourceFlow.Net - Complete Guide

## Table of Contents
1. [Introduction](#introduction)
2. [Core Concepts](#core-concepts)
3. [Architecture Overview](#architecture-overview)
4. [Getting Started](#getting-started)
5. [Framework Components](#framework-components)
6. [Persistence with Entity Framework](#persistence-with-entity-framework)
7. [EntityFramework Usage Examples](#entityframework-usage-examples)
8. [Implementation Guide](#implementation-guide)
9. [Advanced Features](#advanced-features)
10. [Performance and Observability](#performance-and-observability)
11. [Best Practices](#best-practices)
12. [FAQ](#faq)

---

## Introduction

**SourceFlow.Net** is a modern, lightweight, and extensible .NET framework designed for building scalable event-sourced applications using Domain-Driven Design (DDD) principles and Command Query Responsibility Segregation (CQRS) patterns. Built for .NET 8+ with performance and developer experience as core priorities.

### What Makes SourceFlow.Net Special?

SourceFlow.Net provides a complete toolkit for event sourcing, domain modeling, and command/query separation, enabling developers to build maintainable, scalable applications with a strong foundation in proven architectural patterns.

### Key Features

* 🏗️ **Domain-Driven Design Support** - First-class support for aggregates, entities, value objects
* ⚡ **CQRS Implementation** - Complete command/query separation with optimized read models
* 📊 **Event Sourcing Foundation** - Event-first design with full audit trail
* 🧱 **Clean Architecture** - Clear separation of concerns and dependency management
* 💾 **Flexible Persistence** - Multiple storage options including Entity Framework Core
* 🔄 **Event Replay** - Built-in command replay for debugging and state reconstruction
* 🎯 **Type Safety** - Strongly-typed commands, events, and projections
* 📦 **Dependency Injection** - Seamless integration with .NET DI container
* 📈 **OpenTelemetry Integration** - Built-in distributed tracing and metrics for operations at scale
* ⚡ **Memory Optimization** - ArrayPool-based optimization for extreme throughput scenarios
* 🛡️ **Resilience Patterns** - Polly integration for fault tolerance with retry policies and circuit breakers

---

## Core Concepts

### Event Sourcing

**Event Sourcing** is an architectural pattern where the state of an application is determined by a sequence of events. Instead of storing the current state directly, the system stores all the events that have occurred, allowing for complete state reconstruction at any point in time.

#### Key Benefits:
- **Complete Audit Trail**: Every change is recorded as an immutable event
- **Time Travel**: Reconstruct system state at any point in history
- **Debugging**: Full visibility into how the system reached its current state
- **Scalability**: Events can be replayed to build multiple read models

#### Example in SourceFlow.Net:
```csharp
// Events are immutable records of what happened
public class AccountCreated : Event<BankAccount>
{
    public AccountCreated(BankAccount payload) : base(payload) { }
}

public class MoneyDeposited : Event<BankAccount>
{
    public MoneyDeposited(BankAccount payload) : base(payload) { }
}
```

### Domain-Driven Design (DDD)

**Domain-Driven Design** is a software design approach that focuses on modeling software to match the business domain. It emphasizes collaboration between technical and domain experts to create a shared understanding of the problem space.

#### Core DDD Elements in SourceFlow.Net:

**Entities**: Objects with unique identity
```csharp
public class BankAccount : IEntity
{
    public int Id { get; set; }
    public string AccountName { get; set; }
    public decimal Balance { get; set; }
    public bool IsClosed { get; set; }
    public DateTime CreatedOn { get; set; }
}
```

**Aggregates**: Coordinate business logic and ensure consistency
```csharp
public class AccountAggregate : Aggregate<BankAccount>
{
    public void CreateAccount(int accountId, string holder, decimal amount)
    {
        // Business logic validation
        Send(new CreateAccount(new CreateAccountPayload
        {
            Id = accountId,
            AccountName = holder,
            InitialAmount = amount
        }));
    }
}
```

**Sagas**: Orchestrate long-running business processes
```csharp
public class AccountSaga : Saga<BankAccount>, IHandles<CreateAccount>
{
    public async Task Handle(CreateAccount command)
    {
        // Validate, persist, and raise events
        var account = new BankAccount { /* ... */ };
        await repository.Persist(account);
        await Raise(new AccountCreated(account));
    }
}
```

### Command Query Responsibility Segregation (CQRS)

**CQRS** separates read and write operations, allowing for optimized data models for different purposes.

#### Commands: Represent intent to change state
```csharp
public class CreateAccount : Command<CreateAccountPayload>
{
    // Parameterless constructor required for deserialization
    public CreateAccount() : base() { }

    public CreateAccount(CreateAccountPayload payload) : base(payload) { }
}
```

#### Queries: Handled through optimized view models
```csharp
public class AccountViewModel : IViewModel
{
    public int Id { get; set; }
    public string AccountName { get; set; }
    public decimal CurrentBalance { get; set; }
    public DateTime LastUpdated { get; set; }
    public int TransactionCount { get; set; }
}
```

#### Projections: Update read models based on events
```csharp
public class AccountProjection : IProjectOn<AccountCreated>, IProjectOn<MoneyDeposited>
{
    public async Task Apply(AccountCreated @event)
    {
        var view = new AccountViewModel
        {
            Id = @event.Payload.Id,
            AccountName = @event.Payload.AccountName,
            CurrentBalance = @event.Payload.Balance
        };
        await provider.Push(view);
    }
}
```

---

## Architecture Overview

### High-Level Architecture

<img src="https://github.com/CodeShayk/SourceFlow.Net/blob/v1.0.0/Images/Architecture.png" alt="architecture" style="width:1100px; height:400px"/>

### Component Interactions

1. **Aggregates** encapsulate business logic and send commands
2. **Command Bus** routes commands to appropriate saga handlers
3. **Sagas** handle commands and maintain consistency across aggregates
4. **Sagas** persist entities to the **Entity Store**
5. **Sagas** raise events to the **Event Queue**
6. **Event Queue** dispatches events to subscribers
7. **Views** Projections that update read models (ViewModels) based on events
8. **Command Store** persists commands for replay capability
9. **Entity Store** persists root aggregates (entities) within bounded context
10. **ViewModel Store** persists transformed view models from events
  
**Entity Framework Stores** provide persistence using EF Core with support for multiple databases

---

## Getting Started

### Installation

```bash
# Install the core package
dotnet add package SourceFlow

# Install Entity Framework persistence
dotnet add package SourceFlow.Stores.EntityFramework
```

### Basic Setup

```csharp
// Program.cs
using SourceFlow;
using SourceFlow.Stores.EntityFramework;
using SourceFlow.Stores.EntityFramework.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var services = new ServiceCollection();

// Add logging
services.AddLogging(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});

// Register entity and view model types BEFORE building service provider
EntityDbContext.RegisterAssembly(typeof(Program).Assembly);
ViewModelDbContext.RegisterAssembly(typeof(Program).Assembly);

// Configure SourceFlow with automatic discovery
services.UseSourceFlow(typeof(Program).Assembly);

// Add Entity Framework stores with SQL Server (default)
services.AddSourceFlowEfStores(
    "Server=localhost;Database=SourceFlow;Integrated Security=true;TrustServerCertificate=true;");

var serviceProvider = services.BuildServiceProvider();

// Initialize databases
var commandContext = serviceProvider.GetRequiredService<CommandDbContext>();
await commandContext.Database.EnsureCreatedAsync();

var entityContext = serviceProvider.GetRequiredService<EntityDbContext>();
await entityContext.Database.EnsureCreatedAsync();
entityContext.ApplyMigrations();

var viewModelContext = serviceProvider.GetRequiredService<ViewModelDbContext>();
await viewModelContext.Database.EnsureCreatedAsync();
viewModelContext.ApplyMigrations();

// Start using SourceFlow
var aggregateFactory = serviceProvider.GetRequiredService<IAggregateFactory>();
var accountAggregate = await aggregateFactory.Create<IAccountAggregate>();

accountAggregate.CreateAccount(1, "John Doe", 1000m);
```

For other database providers (PostgreSQL, MySQL, SQLite), see [EntityFramework Usage Examples](#entityframework-usage-examples).

---

## Framework Components

### 1. Aggregates

Aggregates are the primary building blocks that encapsulate business logic and coordinate with the command bus.

```csharp
public abstract class Aggregate<TEntity> : IAggregate
    where TEntity : class, IEntity
{
    protected ICommandPublisher commandPublisher;
    protected ILogger logger;

    // Send commands to command bus
    protected async Task Send(ICommand command);

    // Subscribe to external events
    public virtual Task On(IEvent @event);
}
```

**Key Features:**
- Command publishing
- Event subscription for external changes
- Logger integration
- Generic entity support

### 2. Sagas

Sagas handle commands and coordinate business processes, maintaining consistency across aggregate boundaries.

```csharp
public abstract class Saga<TEntity> : ISaga
    where TEntity : class, IEntity
{
    protected IEntityStoreAdapter repository;
    protected ICommandPublisher commandPublisher;
    protected IEventQueue eventQueue;
    protected ILogger logger;

    // Publish commands
    protected async Task Publish<TCommand>(TCommand command);

    // Raise events
    protected async Task Raise<TEvent>(TEvent @event);
}
```

**Key Features:**
- Dynamic command handling via `IHandles<TCommand>`
- Event publishing
- Repository access for persistence
- Built-in logging

### 3. Command Bus

The command bus routes commands to appropriate saga handlers and manages command persistence.

```csharp
public interface ICommandBus
{
    // Publish commands to sagas
    Task Publish<TCommand>(TCommand command) where TCommand : ICommand;

    // Event dispatchers for command lifecycle
    event EventHandler<ICommand> Dispatchers;
}
```

### 4. Event Queue

The event queue manages event flow and dispatches events to subscribers.

```csharp
public interface IEventQueue
{
    // Enqueue events for processing
    Task Enqueue<TEvent>(TEvent @event) where TEvent : IEvent;

    // Event dispatchers
    event EventHandler<IEvent> Dispatchers;
}
```

### 5. Stores (Persistence Layer)

SourceFlow.Net defines three core store interfaces:

#### ICommandStore
Persists commands for event sourcing and replay
```csharp
public interface ICommandStore
{
    Task Save(ICommand command);
    Task<IEnumerable<ICommand>> Load(int entityId);
}
```

#### IEntityStore
Persists domain entities
```csharp
public interface IEntityStore
{
    Task Persist<TEntity>(TEntity entity) where TEntity : class, IEntity;
    Task<TEntity> Get<TEntity>(int id) where TEntity : class, IEntity;
    Task Delete<TEntity>(TEntity entity) where TEntity : class, IEntity;
}
```

#### IViewModelStore
Persists read models (projections)
```csharp
public interface IViewModelStore
{
    Task Persist<TViewModel>(TViewModel model) where TViewModel : class, IViewModel;
    Task<TViewModel> Get<TViewModel>(int id) where TViewModel : class, IViewModel;
    Task Delete<TViewModel>(TViewModel model) where TViewModel : class, IViewModel;
}
```

---

## Persistence with Entity Framework

SourceFlow.Stores.EntityFramework provides production-ready persistence using Entity Framework Core with support for multiple database providers.

### Features

- ✅ **Multiple Database Support**: SQL Server, PostgreSQL, SQLite, and more
- ✅ **Flexible Configuration**: Single or separate connection strings per store
- ✅ **Dynamic Type Registration**: Runtime registration of entities and view models
- ✅ **Migration Support**: Manual table creation bypassing EF Core model caching
- ✅ **Thread-Safe**: Designed for concurrent access
- ✅ **Optimized Tracking**: Proper EF Core change tracking management
- ✅ **Production-Ready Enhancements**: Resilience, observability, and memory optimization

### Installation

```bash
dotnet add package SourceFlow.Stores.EntityFramework
```

### Configuration Options

#### 1. Single Connection String (All Stores)

Use the same database for all stores:

```csharp
services.AddSourceFlowEfStores("Server=localhost;Database=SourceFlow;Integrated Security=true;");
```

#### 2. Separate Connection Strings

Use different databases for each store:

```csharp
services.AddSourceFlowEfStores(
    commandConnectionString: "Server=localhost;Database=SourceFlow_Commands;...",
    entityConnectionString: "Server=localhost;Database=SourceFlow_Entities;...",
    viewModelConnectionString: "Server=localhost;Database=SourceFlow_Views;..."
);
```

#### 3. Configuration-Based

Read from `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "SourceFlow.Default": "Server=localhost;Database=SourceFlow;Integrated Security=true;",
    "SourceFlow.Command": "Server=localhost;Database=Commands;...",
    "SourceFlow.Entity": "Server=localhost;Database=Entities;...",
    "SourceFlow.ViewModel": "Server=localhost;Database=Views;..."
  }
}
```

```csharp
services.AddSourceFlowEfStores(configuration);
```

#### 4. Options Pattern

Configure using options:

```csharp
services.AddSourceFlowEfStores(options =>
{
    options.DefaultConnectionString = "Server=localhost;Database=SourceFlow;...";
    // Or specify individual connection strings
    options.CommandConnectionString = "...";
    options.EntityConnectionString = "...";
    options.ViewModelConnectionString = "...";
});
```

#### 5. Custom Database Provider

Use PostgreSQL, SQLite, or other providers:

```csharp
// PostgreSQL
services.AddSourceFlowEfStoresWithCustomProvider(options =>
    options.UseNpgsql("Host=localhost;Database=sourceflow;Username=postgres;Password=..."));

// SQLite
services.AddSourceFlowEfStoresWithCustomProvider(options =>
    options.UseSqlite("Data Source=sourceflow.db"));

// In-Memory (for testing)
services.AddSourceFlowEfStoresWithCustomProvider(options =>
    options.UseInMemoryDatabase("SourceFlowTest"));
```

#### 6. Different Providers Per Store

Use different database types for each store:

```csharp
services.AddSourceFlowEfStoresWithCustomProviders(
    commandContextConfig: options => options.UseSqlServer("..."),
    entityContextConfig: options => options.UseNpgsql("..."),
    viewModelContextConfig: options => options.UseSqlite("...")
);
```

### Dynamic Type Registration

Entity Framework requires types to be registered before creating the database schema. SourceFlow.Stores.EntityFramework provides multiple registration strategies:

#### 1. Explicit Type Registration

Register specific types before database initialization:

```csharp
// In your startup or test setup
EntityDbContext.RegisterEntityType<BankAccount>();
EntityDbContext.RegisterEntityType<Customer>();

ViewModelDbContext.RegisterViewModelType<AccountViewModel>();
ViewModelDbContext.RegisterViewModelType<CustomerViewModel>();

// Then build service provider and ensure databases are created
var serviceProvider = services.BuildServiceProvider();

var entityContext = serviceProvider.GetRequiredService<EntityDbContext>();
entityContext.Database.EnsureCreated();
entityContext.ApplyMigrations(); // Creates tables for registered types

var viewModelContext = serviceProvider.GetRequiredService<ViewModelDbContext>();
viewModelContext.Database.EnsureCreated();
viewModelContext.ApplyMigrations(); // Creates tables for registered view models
```

#### 2. Assembly Scanning

Register all types from an assembly:

```csharp
// Register the test or application assembly
EntityDbContext.RegisterAssembly(typeof(BankAccount).Assembly);
ViewModelDbContext.RegisterAssembly(typeof(AccountViewModel).Assembly);

var serviceProvider = services.BuildServiceProvider();

// Apply migrations to create tables
var entityContext = serviceProvider.GetRequiredService<EntityDbContext>();
entityContext.Database.EnsureCreated();
entityContext.ApplyMigrations();

var viewModelContext = serviceProvider.GetRequiredService<ViewModelDbContext>();
viewModelContext.Database.EnsureCreated();
viewModelContext.ApplyMigrations();
```

#### 3. Auto-Discovery (Fallback)

The DbContexts automatically discover types from loaded assemblies (fallback mechanism):

```csharp
// Just ensure databases are created
var entityContext = serviceProvider.GetRequiredService<EntityDbContext>();
entityContext.Database.EnsureCreated();

var viewModelContext = serviceProvider.GetRequiredService<ViewModelDbContext>();
viewModelContext.Database.EnsureCreated();

// Note: This may not catch all types reliably; explicit registration is recommended
```

### Table Naming Convention

All dynamically created tables use the `T` prefix:

- `BankAccount` entity → `TBankAccount` table
- `AccountViewModel` → `TAccountViewModel` table
- `Customer` entity → `TCustomer` table

This convention helps distinguish dynamically created tables from EF Core's built-in tables.

### Migration Helper

The `DbContextMigrationHelper` manually creates database schemas, bypassing EF Core's model caching:

```csharp
// Called automatically by ApplyMigrations()
public static void CreateEntityTables(
    EntityDbContext context,
    IEnumerable<Type> entityTypes)
{
    // Creates tables with proper columns and primary keys
    // Supports int, long, string, bool, DateTime, decimal, double, float, byte[], enums
}

public static void CreateViewModelTables(
    ViewModelDbContext context,
    IEnumerable<Type> viewModelTypes)
{
    // Creates tables for view models
}
```

### Complete Setup Example

```csharp
using SourceFlow;
using SourceFlow.Stores.EntityFramework;
using SourceFlow.Stores.EntityFramework.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

var services = new ServiceCollection();

// Add logging
services.AddLogging(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});

// Register types BEFORE building service provider
EntityDbContext.RegisterAssembly(typeof(BankAccount).Assembly);
ViewModelDbContext.RegisterAssembly(typeof(AccountViewModel).Assembly);

// Configure SourceFlow
services.UseSourceFlow(typeof(Program).Assembly);

// Add Entity Framework stores (SQL Server by default)
services.AddSourceFlowEfStores(
    "Server=localhost;Database=SourceFlow;Integrated Security=true;TrustServerCertificate=true;");

// Or use custom provider for other databases:
// services.AddSourceFlowEfStoresWithCustomProvider(options =>
//     options.UseNpgsql("Host=localhost;Database=sourceflow;Username=postgres;Password=..."));

var serviceProvider = services.BuildServiceProvider();

// Ensure all databases are created and migrated
var commandContext = serviceProvider.GetRequiredService<CommandDbContext>();
await commandContext.Database.EnsureCreatedAsync();

var entityContext = serviceProvider.GetRequiredService<EntityDbContext>();
await entityContext.Database.EnsureCreatedAsync();
entityContext.ApplyMigrations(); // Create tables for registered entity types

var viewModelContext = serviceProvider.GetRequiredService<ViewModelDbContext>();
await viewModelContext.Database.EnsureCreatedAsync();
viewModelContext.ApplyMigrations(); // Create tables for registered view model types

// Start using SourceFlow
var aggregateFactory = serviceProvider.GetRequiredService<IAggregateFactory>();
var accountAggregate = await aggregateFactory.Create<IAccountAggregate>();

accountAggregate.CreateAccount(1, "John Doe", 1000m);
```

### Testing with In-Memory Database

For unit and integration tests, use SQLite in-memory databases with proper setup:

```csharp
using NUnit.Framework;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Stores.EntityFramework;
using SourceFlow.Stores.EntityFramework.Extensions;
using SourceFlow.Stores.EntityFramework.Options;
using SourceFlow.Stores.EntityFramework.Services;
using SourceFlow.Stores.EntityFramework.Stores;

[TestFixture]
public class BankAccountIntegrationTests
{
    private ServiceProvider? _serviceProvider;
    private SqliteConnection? _connection;

    [SetUp]
    public void Setup()
    {
        // Clear previous registrations
        EntityDbContext.ClearRegistrations();
        ViewModelDbContext.ClearRegistrations();

        // Register test types
        EntityDbContext.RegisterEntityType<BankAccount>();
        ViewModelDbContext.RegisterViewModelType<AccountViewModel>();

        // Create shared in-memory SQLite connection for all contexts
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();

        // Add logging for better test diagnostics
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        // Configure SQLite with shared connection
        // Use EnableServiceProviderCaching(false) to avoid EF Core 9.0 multiple provider conflicts
        services.AddDbContext<CommandDbContext>(options =>
            options.UseSqlite(_connection)
                .EnableServiceProviderCaching(false));
        services.AddDbContext<EntityDbContext>(options =>
            options.UseSqlite(_connection)
                .EnableServiceProviderCaching(false));
        services.AddDbContext<ViewModelDbContext>(options =>
            options.UseSqlite(_connection)
                .EnableServiceProviderCaching(false));

        // Register SourceFlowEfOptions with default settings
        var efOptions = new SourceFlowEfOptions();
        services.AddSingleton(efOptions);

        // Register common services manually (avoids provider conflicts)
        services.AddScoped<IDatabaseResiliencePolicy, DatabaseResiliencePolicy>();
        services.AddScoped<IDatabaseTelemetryService, DatabaseTelemetryService>();
        services.AddScoped<ICommandStore, EfCommandStore>();
        services.AddScoped<IEntityStore, EfEntityStore>();
        services.AddScoped<IViewModelStore, EfViewModelStore>();

        // Register SourceFlow
        services.UseSourceFlow(Assembly.GetExecutingAssembly());

        _serviceProvider = services.BuildServiceProvider();

        // Create all database schemas
        var commandContext = _serviceProvider.GetRequiredService<CommandDbContext>();
        commandContext.Database.EnsureCreated();

        var entityContext = _serviceProvider.GetRequiredService<EntityDbContext>();
        entityContext.Database.EnsureCreated();
        entityContext.ApplyMigrations();  // Create tables for registered entity types

        var viewModelContext = _serviceProvider.GetRequiredService<ViewModelDbContext>();
        viewModelContext.Database.EnsureCreated();
        viewModelContext.ApplyMigrations();  // Create tables for registered view model types
    }

    [TearDown]
    public void TearDown()
    {
        // Clean up resources
        _connection?.Close();
        _connection?.Dispose();
        _serviceProvider?.Dispose();
    }

    [Test]
    public async Task CreateAccount_StoresInDatabase()
    {
        // Arrange
        var aggregateFactory = _serviceProvider.GetRequiredService<IAggregateFactory>();
        var accountAggregate = await aggregateFactory.Create<IAccountAggregate>();

        // Act
        accountAggregate.CreateAccount(1, "John Doe", 1000m);

        // Wait for async processing
        await Task.Delay(100);

        // Assert
        var entityStore = _serviceProvider.GetRequiredService<IEntityStoreAdapter>();
        var account = await entityStore.Get<BankAccount>(1);

        Assert.That(account, Is.Not.Null);
        Assert.That(account.AccountName, Is.EqualTo("John Doe"));
        Assert.That(account.Balance, Is.EqualTo(1000m));
    }
}
```

---

## EntityFramework Usage Examples

This section provides practical examples for common scenarios using SourceFlow.Stores.EntityFramework.

### Example 1: Simple Console Application with SQL Server

Complete working example for a console application:

```csharp
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SourceFlow;
using SourceFlow.Stores.EntityFramework;
using SourceFlow.Stores.EntityFramework.Extensions;

class Program
{
    static async Task Main(string[] args)
    {
        // Setup service collection
        var services = new ServiceCollection();

        // Add logging
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        // Register entity and view model types BEFORE building service provider
        EntityDbContext.RegisterEntityType<BankAccount>();
        ViewModelDbContext.RegisterViewModelType<AccountViewModel>();

        // Configure SourceFlow
        services.UseSourceFlow(typeof(Program).Assembly);

        // Add Entity Framework stores with SQL Server
        services.AddSourceFlowEfStores(
            "Server=localhost;Database=SourceFlowDemo;Integrated Security=true;TrustServerCertificate=true;");

        var serviceProvider = services.BuildServiceProvider();

        // Ensure databases are created
        var commandContext = serviceProvider.GetRequiredService<CommandDbContext>();
        await commandContext.Database.EnsureCreatedAsync();

        var entityContext = serviceProvider.GetRequiredService<EntityDbContext>();
        await entityContext.Database.EnsureCreatedAsync();
        entityContext.ApplyMigrations();

        var viewModelContext = serviceProvider.GetRequiredService<ViewModelDbContext>();
        await viewModelContext.Database.EnsureCreatedAsync();
        viewModelContext.ApplyMigrations();

        // Use the aggregate
        var aggregateFactory = serviceProvider.GetRequiredService<IAggregateFactory>();
        var accountAggregate = await aggregateFactory.Create<IAccountAggregate>();

        // Execute business operations
        accountAggregate.CreateAccount(1, "Alice Smith", 5000m);
        accountAggregate.Deposit(1, 1500m);
        accountAggregate.Withdraw(1, 500m);

        // Give async processing time to complete
        await Task.Delay(500);

        // Query the read model
        var viewModelStore = serviceProvider.GetRequiredService<IViewModelStoreAdapter>();
        var accountView = await viewModelStore.Find<AccountViewModel>(1);

        Console.WriteLine($"Account: {accountView.AccountName}");
        Console.WriteLine($"Balance: {accountView.CurrentBalance:C}");
        Console.WriteLine($"Transactions: {accountView.TransactionCount}");
        Console.WriteLine($"Created: {accountView.CreatedDate:yyyy-MM-dd}");
    }
}
```

### Example 2: ASP.NET Core Web API with PostgreSQL

Complete setup for a web API using PostgreSQL:

```csharp
// Program.cs
using Microsoft.EntityFrameworkCore;
using SourceFlow;
using SourceFlow.Stores.EntityFramework;
using SourceFlow.Stores.EntityFramework.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Register entity and view model types
EntityDbContext.RegisterAssembly(typeof(Program).Assembly);
ViewModelDbContext.RegisterAssembly(typeof(Program).Assembly);

// Configure SourceFlow with PostgreSQL
builder.Services.UseSourceFlow(typeof(Program).Assembly);

builder.Services.AddSourceFlowEfStoresWithCustomProvider(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("SourceFlow")));

var app = builder.Build();

// Initialize databases on startup
using (var scope = app.Services.CreateScope())
{
    var commandContext = scope.ServiceProvider.GetRequiredService<CommandDbContext>();
    await commandContext.Database.EnsureCreatedAsync();

    var entityContext = scope.ServiceProvider.GetRequiredService<EntityDbContext>();
    await entityContext.Database.EnsureCreatedAsync();
    entityContext.ApplyMigrations();

    var viewModelContext = scope.ServiceProvider.GetRequiredService<ViewModelDbContext>();
    await viewModelContext.Database.EnsureCreatedAsync();
    viewModelContext.ApplyMigrations();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();
app.Run();
```

```csharp
// appsettings.json
{
  "ConnectionStrings": {
    "SourceFlow": "Host=localhost;Database=sourceflow;Username=postgres;Password=yourpassword"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.EntityFrameworkCore": "Warning"
    }
  }
}
```

```csharp
// Controllers/AccountController.cs
using Microsoft.AspNetCore.Mvc;
using SourceFlow;

[ApiController]
[Route("api/[controller]")]
public class AccountController : ControllerBase
{
    private readonly IAggregateFactory _aggregateFactory;
    private readonly IViewModelStoreAdapter _viewModelStore;
    private readonly ILogger<AccountController> _logger;

    public AccountController(
        IAggregateFactory aggregateFactory,
        IViewModelStoreAdapter viewModelStore,
        ILogger<AccountController> logger)
    {
        _aggregateFactory = aggregateFactory;
        _viewModelStore = viewModelStore;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> CreateAccount(CreateAccountRequest request)
    {
        var aggregate = await _aggregateFactory.Create<IAccountAggregate>();
        aggregate.CreateAccount(request.Id, request.AccountName, request.InitialBalance);

        _logger.LogInformation("Account created: {AccountId}", request.Id);
        return CreatedAtAction(nameof(GetAccount), new { id = request.Id }, request);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<AccountViewModel>> GetAccount(int id)
    {
        try
        {
            var account = await _viewModelStore.Find<AccountViewModel>(id);
            return Ok(account);
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
    }

    [HttpPost("{id}/deposit")]
    public async Task<IActionResult> Deposit(int id, [FromBody] TransactionRequest request)
    {
        var aggregate = await _aggregateFactory.Create<IAccountAggregate>();
        aggregate.Deposit(id, request.Amount);

        _logger.LogInformation("Deposited {Amount} to account {AccountId}", request.Amount, id);
        return NoContent();
    }

    [HttpPost("{id}/withdraw")]
    public async Task<IActionResult> Withdraw(int id, [FromBody] TransactionRequest request)
    {
        try
        {
            var aggregate = await _aggregateFactory.Create<IAccountAggregate>();
            aggregate.Withdraw(id, request.Amount);

            _logger.LogInformation("Withdrew {Amount} from account {AccountId}", request.Amount, id);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

public record CreateAccountRequest(int Id, string AccountName, decimal InitialBalance);
public record TransactionRequest(decimal Amount);
```

### Example 3: Microservices with Separate Databases

Using different databases for different stores in a microservices architecture:

```csharp
// Program.cs for Banking Microservice
var builder = WebApplication.CreateBuilder(args);

// Register types
EntityDbContext.RegisterAssembly(typeof(Program).Assembly);
ViewModelDbContext.RegisterAssembly(typeof(Program).Assembly);

// Configure SourceFlow
builder.Services.UseSourceFlow(typeof(Program).Assembly);

// Each store uses a different database optimized for its purpose
builder.Services.AddSourceFlowEfStoresWithCustomProviders(
    // Commands: PostgreSQL with JSONB support for efficient command storage
    commandContextConfig: opt => opt.UseNpgsql(
        builder.Configuration.GetConnectionString("CommandStore")),

    // Entities: SQL Server with optimized indexes for transactional workload
    entityContextConfig: opt => opt.UseSqlServer(
        builder.Configuration.GetConnectionString("EntityStore")),

    // ViewModels: SQLite for fast read queries in read-heavy scenarios
    viewModelContextConfig: opt => opt.UseSqlite(
        builder.Configuration.GetConnectionString("ViewStore"))
);

var app = builder.Build();

// Initialize all databases
using (var scope = app.Services.CreateScope())
{
    var commandContext = scope.ServiceProvider.GetRequiredService<CommandDbContext>();
    await commandContext.Database.MigrateAsync();

    var entityContext = scope.ServiceProvider.GetRequiredService<EntityDbContext>();
    await entityContext.Database.MigrateAsync();
    entityContext.ApplyMigrations();

    var viewModelContext = scope.ServiceProvider.GetRequiredService<ViewModelDbContext>();
    await viewModelContext.Database.MigrateAsync();
    viewModelContext.ApplyMigrations();
}

app.Run();
```

```json
// appsettings.json
{
  "ConnectionStrings": {
    "CommandStore": "Host=postgres-commands.internal;Database=banking_commands;Username=app;Password=...",
    "EntityStore": "Server=sqlserver-entities.internal;Database=banking_entities;User Id=app;Password=...;",
    "ViewStore": "Data Source=/data/banking_views.db"
  }
}
```

### Example 4: Production Configuration with Resilience and Observability

Complete production setup with all enterprise features:

```csharp
// Program.cs
using SourceFlow;
using SourceFlow.Observability;
using SourceFlow.Stores.EntityFramework;
using SourceFlow.Stores.EntityFramework.Extensions;
using OpenTelemetry;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Register domain types
EntityDbContext.RegisterAssembly(typeof(Program).Assembly);
ViewModelDbContext.RegisterAssembly(typeof(Program).Assembly);

// Configure SourceFlow with observability
builder.Services.AddSourceFlowTelemetry(options =>
{
    options.Enabled = true;
    options.ServiceName = "BankingService";
    options.ServiceVersion = builder.Configuration["AppVersion"] ?? "1.0.0";
});

// Configure OpenTelemetry exporters
builder.Services.AddOpenTelemetry()
    .AddSourceFlowOtlpExporter(builder.Configuration["Observability:OtlpEndpoint"])
    .AddSourceFlowResourceAttributes(
        ("environment", builder.Environment.EnvironmentName),
        ("deployment.region", builder.Configuration["Deployment:Region"]),
        ("service.instance.id", Environment.MachineName)
    )
    .ConfigureSourceFlowBatchProcessing(
        maxQueueSize: 2048,
        maxExportBatchSize: 512,
        scheduledDelayMilliseconds: 5000
    );

// Register SourceFlow
builder.Services.UseSourceFlow(typeof(Program).Assembly);

// Configure Entity Framework stores with resilience and observability
builder.Services.AddSourceFlowEfStores(options =>
{
    options.DefaultConnectionString = builder.Configuration.GetConnectionString("SourceFlow");

    // Resilience configuration
    options.Resilience.Enabled = true;
    options.Resilience.Retry.MaxRetryAttempts = 3;
    options.Resilience.Retry.BaseDelayMs = 1000;
    options.Resilience.Retry.UseExponentialBackoff = true;
    options.Resilience.Retry.UseJitter = true;
    options.Resilience.CircuitBreaker.Enabled = true;
    options.Resilience.CircuitBreaker.FailureThreshold = 10;
    options.Resilience.CircuitBreaker.BreakDurationMs = 60000;
    options.Resilience.Timeout.Enabled = true;
    options.Resilience.Timeout.TimeoutMs = 30000;

    // Observability configuration
    options.Observability.Enabled = true;
    options.Observability.ServiceName = "BankingService.EntityFramework";
    options.Observability.Tracing.Enabled = true;
    options.Observability.Tracing.TraceDatabaseOperations = true;
    options.Observability.Tracing.IncludeSqlInTraces = false;  // Don't log SQL in production
    options.Observability.Tracing.SamplingRatio = 0.1;         // Sample 10%
    options.Observability.Metrics.Enabled = true;
    options.Observability.Metrics.CollectDatabaseMetrics = true;

    // Table naming conventions
    options.EntityTableNaming.Casing = TableNameCasing.SnakeCase;
    options.EntityTableNaming.Pluralize = true;
    options.ViewModelTableNaming.Casing = TableNameCasing.SnakeCase;
    options.ViewModelTableNaming.Suffix = "_view";
});

// Add health checks
builder.Services.AddHealthChecks()
    .AddDbContextCheck<CommandDbContext>("command-store")
    .AddDbContextCheck<EntityDbContext>("entity-store")
    .AddDbContextCheck<ViewModelDbContext>("viewmodel-store");

var app = builder.Build();

// Initialize databases
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    try
    {
        var commandContext = scope.ServiceProvider.GetRequiredService<CommandDbContext>();
        await commandContext.Database.EnsureCreatedAsync();
        logger.LogInformation("Command store initialized");

        var entityContext = scope.ServiceProvider.GetRequiredService<EntityDbContext>();
        await entityContext.Database.EnsureCreatedAsync();
        entityContext.ApplyMigrations();
        logger.LogInformation("Entity store initialized");

        var viewModelContext = scope.ServiceProvider.GetRequiredService<ViewModelDbContext>();
        await viewModelContext.Database.EnsureCreatedAsync();
        viewModelContext.ApplyMigrations();
        logger.LogInformation("ViewModel store initialized");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to initialize databases");
        throw;
    }
}

app.MapHealthChecks("/health");
app.Run();
```

```json
// appsettings.Production.json
{
  "ConnectionStrings": {
    "SourceFlow": "Server=prod-db.internal;Database=BankingService;User Id=app_user;Password=...;Max Pool Size=100;Min Pool Size=10;"
  },
  "Observability": {
    "OtlpEndpoint": "http://otel-collector.internal:4317"
  },
  "Deployment": {
    "Region": "us-east-1"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.EntityFrameworkCore": "Warning",
      "SourceFlow": "Information"
    }
  }
}
```

### Example 5: Custom Table Naming with Schema Organization

Organizing tables with custom naming conventions and schemas:

```csharp
services.AddSourceFlowEfStores(options =>
{
    options.DefaultConnectionString = connectionString;

    // Command store: Audit schema with snake_case
    options.CommandTableNaming.UseSchema = true;
    options.CommandTableNaming.SchemaName = "audit";
    options.CommandTableNaming.Casing = TableNameCasing.SnakeCase;
    // Results in: audit.command_record

    // Entity store: Domain schema with pluralized tables
    options.EntityTableNaming.UseSchema = true;
    options.EntityTableNaming.SchemaName = "domain";
    options.EntityTableNaming.Casing = TableNameCasing.SnakeCase;
    options.EntityTableNaming.Pluralize = true;
    // BankAccount -> domain.bank_accounts

    // ViewModel store: Reporting schema with view suffix
    options.ViewModelTableNaming.UseSchema = true;
    options.ViewModelTableNaming.SchemaName = "reporting";
    options.ViewModelTableNaming.Casing = TableNameCasing.SnakeCase;
    options.ViewModelTableNaming.Suffix = "_view";
    options.ViewModelTableNaming.Pluralize = true;
    // AccountViewModel -> reporting.account_views
});

// Create schemas before initializing
var entityContext = serviceProvider.GetRequiredService<EntityDbContext>();
await entityContext.Database.ExecuteSqlRawAsync("CREATE SCHEMA IF NOT EXISTS audit");
await entityContext.Database.ExecuteSqlRawAsync("CREATE SCHEMA IF NOT EXISTS domain");
await entityContext.Database.ExecuteSqlRawAsync("CREATE SCHEMA IF NOT EXISTS reporting");
await entityContext.Database.EnsureCreatedAsync();
entityContext.ApplyMigrations();
```

### Example 6: Background Service Processing Commands

Processing commands in a background service:

```csharp
public class CommandProcessorBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CommandProcessorBackgroundService> _logger;

    public CommandProcessorBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<CommandProcessorBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Command Processor Background Service starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();

                var commandStore = scope.ServiceProvider.GetRequiredService<ICommandStoreAdapter>();
                var aggregateFactory = scope.ServiceProvider.GetRequiredService<IAggregateFactory>();

                // Process any pending commands (example: replay for specific entities)
                var entityIds = await GetPendingEntityIds();

                foreach (var entityId in entityIds)
                {
                    var commands = await commandStore.Retrieve(entityId);

                    if (commands.Any())
                    {
                        _logger.LogInformation(
                            "Processing {Count} commands for entity {EntityId}",
                            commands.Count(), entityId);

                        // Replay commands to rebuild state
                        var aggregate = await aggregateFactory.Create<IAccountAggregate>();
                        // Process commands...
                    }
                }

                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in command processor");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }

        _logger.LogInformation("Command Processor Background Service stopping");
    }

    private async Task<List<int>> GetPendingEntityIds()
    {
        // Implementation to identify entities needing processing
        return new List<int>();
    }
}

// Register in Program.cs
builder.Services.AddHostedService<CommandProcessorBackgroundService>();
```

### Example 7: Multi-Tenant Setup with Database Per Tenant

Implementing multi-tenancy with separate databases:

```csharp
// ITenantProvider.cs
public interface ITenantProvider
{
    string GetCurrentTenantId();
    string GetConnectionString(string tenantId);
}

// TenantDbContextFactory.cs
public class TenantDbContextFactory<TContext> where TContext : DbContext
{
    private readonly ITenantProvider _tenantProvider;
    private readonly IServiceProvider _serviceProvider;

    public TenantDbContextFactory(ITenantProvider tenantProvider, IServiceProvider serviceProvider)
    {
        _tenantProvider = tenantProvider;
        _serviceProvider = serviceProvider;
    }

    public TContext CreateDbContext()
    {
        var tenantId = _tenantProvider.GetCurrentTenantId();
        var connectionString = _tenantProvider.GetConnectionString(tenantId);

        var optionsBuilder = new DbContextOptionsBuilder<TContext>();
        optionsBuilder.UseSqlServer(connectionString);

        return (TContext)Activator.CreateInstance(typeof(TContext), optionsBuilder.Options);
    }
}

// Program.cs
builder.Services.AddScoped<ITenantProvider, HttpContextTenantProvider>();

// Register SourceFlow with multi-tenant support
builder.Services.UseSourceFlow(typeof(Program).Assembly);

// Custom multi-tenant store registration
builder.Services.AddScoped(sp =>
{
    var tenantProvider = sp.GetRequiredService<ITenantProvider>();
    var tenantId = tenantProvider.GetCurrentTenantId();
    var connectionString = tenantProvider.GetConnectionString(tenantId);

    var optionsBuilder = new DbContextOptionsBuilder<EntityDbContext>();
    optionsBuilder.UseSqlServer(connectionString);

    return new EntityDbContext(optionsBuilder.Options);
});

// Similar for CommandDbContext and ViewModelDbContext
```

---

## Implementation Guide

### Creating a Complete Feature

Let's implement a complete banking feature using SourceFlow.Net with Entity Framework persistence:

#### 1. Define Domain Objects

```csharp
// Entity
public class BankAccount : IEntity
{
    public int Id { get; set; }
    public string AccountName { get; set; }
    public decimal Balance { get; set; }
    public bool IsClosed { get; set; }
    public DateTime CreatedOn { get; set; }
    public DateTime ActiveOn { get; set; }
    public string ClosureReason { get; set; }
}

// Command Payloads
public class CreateAccountPayload : IPayload
{
    public int Id { get; set; }
    public string AccountName { get; set; }
    public decimal InitialAmount { get; set; }
}

public class TransactionPayload : IPayload
{
    public int Id { get; set; }
    public decimal Amount { get; set; }
}
```

#### 2. Create Commands

```csharp
public class CreateAccount : Command<CreateAccountPayload>
{
    // Parameterless constructor required for command deserialization from store
    public CreateAccount() : base() { }

    public CreateAccount(CreateAccountPayload payload) : base(payload) { }
}

public class DepositMoney : Command<TransactionPayload>
{
    // Parameterless constructor required for command deserialization from store
    public DepositMoney() : base() { }

    public DepositMoney(TransactionPayload payload) : base(payload) { }
}

public class WithdrawMoney : Command<TransactionPayload>
{
    // Parameterless constructor required for command deserialization from store
    public WithdrawMoney() : base() { }

    public WithdrawMoney(TransactionPayload payload) : base(payload) { }
}
```

#### 3. Define Events

```csharp
public class AccountCreated : Event<BankAccount>
{
    public AccountCreated(BankAccount payload) : base(payload) { }
}

public class MoneyDeposited : Event<BankAccount>
{
    public MoneyDeposited(BankAccount payload) : base(payload) { }
}

public class MoneyWithdrawn : Event<BankAccount>
{
    public MoneyWithdrawn(BankAccount payload) : base(payload) { }
}
```

#### 4. Implement Saga

```csharp
public class AccountSaga : Saga<BankAccount>,
                           IHandles<CreateAccount>,
                           IHandles<DepositMoney>,
                           IHandles<WithdrawMoney>
{
    public async Task Handle(CreateAccount command)
    {
        // Validation
        if (string.IsNullOrEmpty(command.Payload.AccountName))
            throw new ArgumentException("Account name is required");

        if (command.Payload.InitialAmount <= 0)
            throw new ArgumentException("Initial amount must be positive");

        // Create entity
        var account = new BankAccount
        {
            Id = command.Payload.Id,
            AccountName = command.Payload.AccountName,
            Balance = command.Payload.InitialAmount,
            CreatedOn = DateTime.UtcNow,
            ActiveOn = DateTime.UtcNow
        };

        // Persist to Entity Store
        await repository.Persist(account);

        // Raise event
        await Raise(new AccountCreated(account));

        logger.LogInformation("Account created: {AccountId} for {Holder} with balance {Balance}",
            account.Id, account.AccountName, account.Balance);
    }

    public async Task Handle(DepositMoney command)
    {
        var account = await repository.Get<BankAccount>(command.Payload.Id);

        if (account.IsClosed)
            throw new InvalidOperationException("Cannot deposit to closed account");

        account.Balance += command.Payload.Amount;
        await repository.Persist(account);
        await Raise(new MoneyDeposited(account));

        logger.LogInformation("Deposited {Amount} to account {AccountId}. New balance: {Balance}",
            command.Payload.Amount, account.Id, account.Balance);
    }

    public async Task Handle(WithdrawMoney command)
    {
        var account = await repository.Get<BankAccount>(command.Payload.Id);

        if (account.IsClosed)
            throw new InvalidOperationException("Cannot withdraw from closed account");

        if (account.Balance < command.Payload.Amount)
            throw new InvalidOperationException("Insufficient funds");

        account.Balance -= command.Payload.Amount;
        await repository.Persist(account);
        await Raise(new MoneyWithdrawn(account));

        logger.LogInformation("Withdrew {Amount} from account {AccountId}. New balance: {Balance}",
            command.Payload.Amount, account.Id, account.Balance);
    }
}
```

#### 5. Create Aggregate

```csharp
public interface IAccountAggregate : IAggregate
{
    void CreateAccount(int accountId, string holder, decimal amount);
    void Deposit(int accountId, decimal amount);
    void Withdraw(int accountId, decimal amount);
}

public class AccountAggregate : Aggregate<BankAccount>, IAccountAggregate
{
    public void CreateAccount(int accountId, string holder, decimal amount)
    {
        Send(new CreateAccount(new CreateAccountPayload
        {
            Id = accountId,
            AccountName = holder,
            InitialAmount = amount
        }));
    }

    public void Deposit(int accountId, decimal amount)
    {
        Send(new DepositMoney(new TransactionPayload
        {
            Id = accountId,
            Amount = amount
        }));
    }

    public void Withdraw(int accountId, decimal amount)
    {
        Send(new WithdrawMoney(new TransactionPayload
        {
            Id = accountId,
            Amount = amount
        }));
    }
}
```

#### 6. Build Read Models

```csharp
public class AccountViewModel : IViewModel
{
    public int Id { get; set; }
    public string AccountName { get; set; }
    public decimal CurrentBalance { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime LastUpdated { get; set; }
    public int TransactionCount { get; set; }
    public bool IsClosed { get; set; }
    public string ClosureReason { get; set; }
    public int Version { get; set; }
    public DateTime ActiveOn { get; set; }
}

public class AccountView : View,
                           IProjectOn<AccountCreated>,
                           IProjectOn<MoneyDeposited>,
                           IProjectOn<MoneyWithdrawn>
{
    public async Task Apply(AccountCreated @event)
    {
        var view = new AccountViewModel
        {
            Id = @event.Payload.Id,
            AccountName = @event.Payload.AccountName,
            CurrentBalance = @event.Payload.Balance,
            CreatedDate = @event.Payload.CreatedOn,
            LastUpdated = DateTime.UtcNow,
            TransactionCount = 0,
            IsClosed = false,
            ActiveOn = @event.Payload.ActiveOn
        };

        await provider.Push(view);

        logger.LogInformation("Created view model for account {AccountId}", view.Id);
    }

    public async Task Apply(MoneyDeposited @event)
    {
        var view = await provider.Find<AccountViewModel>(@event.Payload.Id);
        view.CurrentBalance = @event.Payload.Balance;
        view.TransactionCount++;
        view.LastUpdated = DateTime.UtcNow;

        await provider.Push(view);

        logger.LogInformation("Updated view model for account {AccountId} after deposit", view.Id);
    }

    public async Task Apply(MoneyWithdrawn @event)
    {
        var view = await provider.Find<AccountViewModel>(@event.Payload.Id);
        view.CurrentBalance = @event.Payload.Balance;
        view.TransactionCount++;
        view.LastUpdated = DateTime.UtcNow;

        await provider.Push(view);

        logger.LogInformation("Updated view model for account {AccountId} after withdrawal", view.Id);
    }
}
```

#### 7. Application Setup

```csharp
// Program.cs
using SourceFlow;
using SourceFlow.Stores.EntityFramework;
using SourceFlow.Stores.EntityFramework.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var services = new ServiceCollection();

// Add logging
services.AddLogging(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});

// Register entity and view model types BEFORE building service provider
EntityDbContext.RegisterEntityType<BankAccount>();
ViewModelDbContext.RegisterViewModelType<AccountViewModel>();

// Configure SourceFlow
services.UseSourceFlow(typeof(Program).Assembly);

// Add Entity Framework stores
services.AddSourceFlowEfStores(
    "Server=localhost;Database=SourceFlow;Integrated Security=true;TrustServerCertificate=true;");

var serviceProvider = services.BuildServiceProvider();

// Ensure databases are created and migrated
var commandContext = serviceProvider.GetRequiredService<CommandDbContext>();
await commandContext.Database.EnsureCreatedAsync();

var entityContext = serviceProvider.GetRequiredService<EntityDbContext>();
await entityContext.Database.EnsureCreatedAsync();
entityContext.ApplyMigrations();

var viewModelContext = serviceProvider.GetRequiredService<ViewModelDbContext>();
await viewModelContext.Database.EnsureCreatedAsync();
viewModelContext.ApplyMigrations();

// Use the aggregate
var aggregateFactory = serviceProvider.GetRequiredService<IAggregateFactory>();
var accountAggregate = await aggregateFactory.Create<IAccountAggregate>();

accountAggregate.CreateAccount(999, "John Doe", 1000m);
accountAggregate.Deposit(999, 500m);
accountAggregate.Withdraw(999, 200m);

// Give async processing time to complete
await Task.Delay(500);

// Query the read model
var viewModelStore = serviceProvider.GetRequiredService<IViewModelStoreAdapter>();
var accountView = await viewModelStore.Find<AccountViewModel>(999);

Console.WriteLine($"Account: {accountView.AccountName}");
Console.WriteLine($"Balance: {accountView.CurrentBalance:C}");
Console.WriteLine($"Transactions: {accountView.TransactionCount}");
```

For complete examples including ASP.NET Core, PostgreSQL, and production configurations, see [EntityFramework Usage Examples](#entityframework-usage-examples).

---

## Advanced Features

### Event Replay

SourceFlow.Net provides built-in command replay functionality for debugging and state reconstruction:

```csharp
var accountAggregate = serviceProvider.GetRequiredService<IAccountAggregate>();

// Replay all commands for an aggregate
await accountAggregate.ReplayHistory(accountId);

// The framework automatically handles:
// 1. Loading commands from store
// 2. Marking commands as replay
// 3. Re-executing command handlers
// 4. Updating projections
```

### Metadata and Auditing

Every command and event includes rich metadata to add producer and consumer centric custom properties.

```csharp
public interface IMetadata
{
    Guid EventId { get; set; }
    bool IsReplay { get; set; }
    DateTime OccurredOn { get; set; }
    int SequenceNo { get; set; }
    IDictionary<string, object> Properties { get; set; }
}
```

### Store Adapters

SourceFlow provides high-level adapters for common operations:

```csharp
// Entity Store Adapter
public interface IEntityStoreAdapter
{
    Task Persist<TEntity>(TEntity entity) where TEntity : class, IEntity;
    Task<TEntity> Get<TEntity>(int id) where TEntity : class, IEntity;
}

// ViewModel Store Adapter
public interface IViewModelStoreAdapter
{
    Task Push<TViewModel>(TViewModel model) where TViewModel : class, IViewModel;
    Task<TViewModel> Find<TViewModel>(int id) where TViewModel : class, IViewModel;
}

// Command Store Adapter
public interface ICommandStoreAdapter
{
    Task Commit(ICommand command);
    Task<IEnumerable<ICommand>> Retrieve(int entityId);
}
```

---

## Performance and Observability

SourceFlow.Net includes comprehensive production-ready features for monitoring, fault tolerance, and high-performance scenarios.

### OpenTelemetry Integration

Built-in support for distributed tracing and metrics collection at scale.

#### Features

- **Distributed Tracing**: Automatically track command execution, event dispatching, and store operations
- **Metrics Collection**: Monitor command rates, saga executions, entity creations, and operation durations
- **Multiple Exporters**: Support for Console, OTLP (Jaeger, Zipkin), and custom exporters
- **Minimal Overhead**: <1ms latency impact, <2% CPU overhead

#### Quick Setup

**Development (Console Exporter):**
```csharp
using SourceFlow.Observability;
using OpenTelemetry;

var services = new ServiceCollection();

// Enable observability with console output
services.AddSourceFlowTelemetry(
    serviceName: "MyEventSourcedApp",
    serviceVersion: "1.0.0");

services.AddOpenTelemetry()
    .AddSourceFlowConsoleExporter();

services.UseSourceFlow();
```

**Production (OTLP Exporter):**
```csharp
services.AddSourceFlowTelemetry(options =>
{
    options.Enabled = true;
    options.ServiceName = "ProductionApp";
    options.ServiceVersion = "1.0.0";
});

services.AddOpenTelemetry()
    .AddSourceFlowOtlpExporter("http://localhost:4317")
    .AddSourceFlowResourceAttributes(
        ("environment", "production"),
        ("region", "us-east-1")
    );
```

#### Instrumented Operations

All core operations are automatically traced:

**Command Operations:**
- `sourceflow.commandbus.dispatch` - Command dispatch and persistence
- `sourceflow.commanddispatcher.send` - Command distribution to sagas
- `sourceflow.domain.command.append` - Command persistence
- `sourceflow.domain.command.load` - Command loading

**Event Operations:**
- `sourceflow.eventqueue.enqueue` - Event queuing
- `sourceflow.eventdispatcher.dispatch` - Event distribution

**Store Operations:**
- `sourceflow.entitystore.persist` / `get` / `delete` - Entity operations
- `sourceflow.viewmodelstore.persist` / `find` / `delete` - ViewModel operations

#### Metrics Collected

- `sourceflow.domain.commands.executed` - Counter of executed commands
- `sourceflow.domain.sagas.executed` - Counter of saga executions
- `sourceflow.domain.entities.created` - Counter of entity creations
- `sourceflow.domain.operation.duration` - Histogram of operation durations (ms)
- `sourceflow.domain.serialization.duration` - Histogram of serialization performance

#### Integration with Existing Telemetry

```csharp
services.AddOpenTelemetry()
    .WithTracing(builder => builder
        .AddSource("SourceFlow.Domain")  // Add SourceFlow
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(builder => builder
        .AddMeter("SourceFlow.Domain")   // Add SourceFlow
        .AddAspNetCoreInstrumentation()
        .AddPrometheusExporter());
```

### ArrayPool Memory Optimization

Dramatically reduce memory allocations in high-throughput scenarios using `ArrayPool<T>`.

#### Performance Benefits

**Memory Allocation Reduction:**
- Before: ~40MB allocations for 10,000 commands
- After: <1MB allocations for 10,000 commands
- **Result: ~40x reduction in allocations**

**GC Pressure Reduction:**
- Gen 0 Collections: ↓70%
- Gen 1 Collections: ↓50%
- Gen 2 Collections: ↓30%

**Throughput Improvements:**
- Command Throughput: +25-40%
- Event Dispatching: +30-50%
- Serialization: +20-35%

#### Features

- **Task Buffer Pooling**: Reduces allocations in parallel task execution
- **JSON Serialization Pooling**: Reuses byte buffers for JSON operations
- **Zero Configuration**: Works automatically, no code changes required
- **Production Tested**: Optimized for extreme throughput scenarios

#### Optimized Components

**TaskBufferPool:**
- Pools task arrays for parallel execution
- Used in `CommandDispatcher` and `EventDispatcher`
- Automatic buffer rental and return

**ByteArrayPool:**
- Pools byte arrays for JSON serialization
- Used in `CommandStoreAdapter`
- Custom `IBufferWriter<byte>` implementation

ArrayPool optimizations are automatically applied to:
- Command serialization/deserialization
- Event dispatching (parallel task execution)
- Store adapter operations

### Resilience with Polly (Entity Framework)

The Entity Framework integration includes Polly-based resilience patterns for fault tolerance.

#### Features

- **Retry Policy**: Automatic retry with exponential backoff and jitter
- **Circuit Breaker**: Prevents cascading failures
- **Timeout Policy**: Enforces maximum execution time

#### Configuration

```csharp
services.AddSourceFlowEfStores(options =>
{
    options.DefaultConnectionString = connectionString;

    // Enable resilience
    options.Resilience.Enabled = true;

    // Retry configuration
    options.Resilience.Retry.MaxRetryAttempts = 3;
    options.Resilience.Retry.BaseDelayMs = 1000;
    options.Resilience.Retry.UseExponentialBackoff = true;
    options.Resilience.Retry.UseJitter = true;

    // Circuit breaker configuration
    options.Resilience.CircuitBreaker.Enabled = true;
    options.Resilience.CircuitBreaker.FailureThreshold = 5;
    options.Resilience.CircuitBreaker.BreakDurationMs = 30000;

    // Timeout configuration
    options.Resilience.Timeout.Enabled = true;
    options.Resilience.Timeout.TimeoutMs = 30000;
});
```

#### Benefits

- **Transient Failure Handling**: Automatically recovers from temporary issues
- **Cascading Failure Prevention**: Circuit breaker stops calling failing services
- **Resource Protection**: Timeouts prevent hanging operations
- **Self-Healing**: System automatically recovers when service becomes available

### Entity Framework Observability

Additional observability features specific to Entity Framework stores.

#### Configuration

```csharp
services.AddSourceFlowEfStores(options =>
{
    options.DefaultConnectionString = connectionString;

    // Configure observability
    options.Observability.Enabled = true;
    options.Observability.ServiceName = "MyApplication";
    options.Observability.ServiceVersion = "1.0.0";

    // Tracing configuration
    options.Observability.Tracing.Enabled = true;
    options.Observability.Tracing.TraceDatabaseOperations = true;
    options.Observability.Tracing.IncludeSqlInTraces = false;  // Enable for debugging
    options.Observability.Tracing.SamplingRatio = 0.1;         // Sample 10% in production

    // Metrics configuration
    options.Observability.Metrics.Enabled = true;
    options.Observability.Metrics.CollectDatabaseMetrics = true;
});

// Configure exporters
services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource("SourceFlow.EntityFramework")
        .AddEntityFrameworkCoreInstrumentation()
        .AddJaegerExporter())
    .WithMetrics(metrics => metrics
        .AddMeter("SourceFlow.EntityFramework")
        .AddPrometheusExporter());
```

#### Additional Traces

- `sourceflow.ef.command.append` - EF command storage
- `sourceflow.ef.command.load` - EF command loading
- `sourceflow.ef.entity.persist` - EF entity persistence
- `sourceflow.ef.viewmodel.persist` - EF view model persistence

#### Additional Metrics

- `sourceflow.commands.appended` - EF command append counter
- `sourceflow.commands.loaded` - EF command load counter
- `sourceflow.entities.persisted` - EF entity persistence counter
- `sourceflow.viewmodels.persisted` - EF view model persistence counter
- `sourceflow.database.connections` - Active connection gauge

### Production Configuration Examples

**Development:**
```csharp
services.AddSourceFlowTelemetry("DevApp", "1.0.0");
services.AddOpenTelemetry()
    .AddSourceFlowConsoleExporter();

services.AddSourceFlowEfStores(options =>
{
    options.DefaultConnectionString = "Data Source=dev.db";
    options.Resilience.Enabled = false;  // Easier debugging
    options.Observability.Enabled = true;
    options.Observability.Tracing.IncludeSqlInTraces = true;
    options.Observability.Tracing.SamplingRatio = 1.0;  // Trace everything
});
```

**Production:**
```csharp
services.AddSourceFlowTelemetry(options =>
{
    options.Enabled = true;
    options.ServiceName = "ProductionApp";
    options.ServiceVersion = "1.0.0";
});

services.AddOpenTelemetry()
    .AddSourceFlowOtlpExporter(otlpEndpoint)
    .AddSourceFlowResourceAttributes(
        ("environment", "production"),
        ("deployment.region", region)
    );

services.AddSourceFlowEfStores(options =>
{
    options.DefaultConnectionString = connectionString;

    // Production resilience settings
    options.Resilience.Enabled = true;
    options.Resilience.Retry.MaxRetryAttempts = 3;
    options.Resilience.CircuitBreaker.Enabled = true;
    options.Resilience.CircuitBreaker.FailureThreshold = 10;

    // Production observability settings
    options.Observability.Enabled = true;
    options.Observability.Tracing.IncludeSqlInTraces = false;
    options.Observability.Tracing.SamplingRatio = 0.1;  // Sample 10%
});
```

**High-Throughput:**
```csharp
services.AddSourceFlowTelemetry(options =>
{
    options.Enabled = true;
    options.ServiceName = "HighThroughputApp";
});

services.AddOpenTelemetry()
    .AddSourceFlowOtlpExporter(otlpEndpoint)
    .ConfigureSourceFlowBatchProcessing(
        maxQueueSize: 2048,
        maxExportBatchSize: 512,
        scheduledDelayMilliseconds: 5000
    );

services.AddSourceFlowEfStores(options =>
{
    options.DefaultConnectionString = connectionString;

    // Optimized for throughput
    options.Resilience.Enabled = true;
    options.Resilience.Retry.MaxRetryAttempts = 2;
    options.Resilience.Retry.BaseDelayMs = 500;

    // Reduced overhead
    options.Observability.Enabled = true;
    options.Observability.Tracing.SamplingRatio = 0.01;  // Sample 1%
});

// ArrayPool optimizations are automatically applied
```

### Monitoring Dashboard Queries

Use these queries in Grafana/Prometheus:

**Average Command Processing Time:**
```promql
rate(sourceflow_domain_operation_duration_sum{operation="sourceflow.commandbus.dispatch"}[5m])
/ rate(sourceflow_domain_operation_duration_count{operation="sourceflow.commandbus.dispatch"}[5m])
```

**Command Throughput:**
```promql
rate(sourceflow_domain_commands_executed[5m])
```

**Serialization Performance (P95):**
```promql
histogram_quantile(0.95,
  rate(sourceflow_domain_serialization_duration_bucket[5m])
)
```

### Package Dependencies

**Core SourceFlow:**
- `OpenTelemetry` (1.14.0)
- `OpenTelemetry.Api` (1.14.0)
- `OpenTelemetry.Exporter.Console` (1.14.0)
- `OpenTelemetry.Exporter.OpenTelemetryProtocol` (1.14.0)
- `OpenTelemetry.Extensions.Hosting` (1.14.0)
- `Microsoft.Extensions.DependencyInjection.Abstractions` (10.0.0)
- `Microsoft.Extensions.Logging.Abstractions` (10.0.0)

**Entity Framework Stores:**
- `Microsoft.EntityFrameworkCore` (9.0.0)
- `Polly` (8.4.2) - For resilience patterns
- `OpenTelemetry.Instrumentation.EntityFrameworkCore` (1.0.0-beta.12)

All packages are free from known vulnerabilities (as of November 2025).

### Additional Resources

- **OpenTelemetry Documentation**: [https://opentelemetry.io/docs/](https://opentelemetry.io/docs/)
- **ArrayPool Documentation**: [https://docs.microsoft.com/en-us/dotnet/api/system.buffers.arraypool-1](https://docs.microsoft.com/en-us/dotnet/api/system.buffers.arraypool-1)
- **Polly Documentation**: [https://github.com/App-vNext/Polly](https://github.com/App-vNext/Polly)
- **SourceFlow.Net OBSERVABILITY_AND_PERFORMANCE.md**: Detailed performance documentation
- **SourceFlow.Stores.EntityFramework ENHANCEMENTS.md**: EF-specific enhancements guide

---

## Best Practices

### 1. Command Design

**Always include a parameterless constructor** for serialization support:

```csharp
// ✅ Good: Specific, intention-revealing commands with proper constructors
public class WithdrawMoney : Command<WithdrawPayload>
{
    // Required for deserialization from command store
    public WithdrawMoney() : base() { }

    public WithdrawMoney(WithdrawPayload payload) : base(payload) { }
}

public class DepositMoney : Command<DepositPayload>
{
    // Required for deserialization from command store
    public DepositMoney() : base() { }

    public DepositMoney(DepositPayload payload) : base(payload) { }
}

// ❌ Bad: Generic, unclear commands
public class UpdateAccount : Command<AccountPayload> { }

// ❌ Bad: Missing parameterless constructor
public class TransferMoney : Command<TransferPayload>
{
    public TransferMoney(TransferPayload payload) : base(payload) { }
    // This command cannot be deserialized from the command store!
}
```

**Key Requirements:**
- Use specific, intention-revealing names
- Always include a public parameterless constructor
- Include a constructor that accepts the payload
- Keep commands immutable after creation

### 2. Event Granularity

```csharp
// ✅ Good: Fine-grained, specific events
public class AccountCreated : Event<BankAccount> { }
public class AccountCredited : Event<BankAccount> { }
public class AccountDebited : Event<BankAccount> { }

// ❌ Bad: Coarse-grained, generic events
public class AccountChanged : Event<BankAccount> { }
```

### 3. Saga Responsibility

```csharp
// ✅ Good: Single responsibility
public class AccountSaga : Saga<BankAccount>,
                           IHandles<CreateAccount>,
                           IHandles<CloseAccount>
{
    // Handles account lifecycle only
}

// ❌ Bad: Multiple responsibilities
public class MegaSaga : Saga<BankAccount>,
                        IHandles<CreateAccount>,
                        IHandles<ProcessLoan>,
                        IHandles<SendEmail>
{
    // Too many responsibilities
}
```

### 4. Type Registration

```csharp
// ✅ Good: Register types early, before building service provider
EntityDbContext.RegisterAssembly(typeof(BankAccount).Assembly);
ViewModelDbContext.RegisterAssembly(typeof(AccountViewModel).Assembly);

var serviceProvider = services.BuildServiceProvider();

// Apply migrations after database creation
entityContext.Database.EnsureCreated();
entityContext.ApplyMigrations();

// ❌ Bad: Relying solely on auto-discovery
var serviceProvider = services.BuildServiceProvider();
// Types may not be discovered reliably
```

### 5. Error Handling

```csharp
public class AccountSaga : Saga<BankAccount>
{
    public async Task Handle(WithdrawMoney command)
    {
        try
        {
            var account = await repository.Get<BankAccount>(command.Payload.Id);

            // Validate business rules
            if (account.IsClosed)
                throw new AccountClosedException($"Account {account.Id} is closed");

            if (account.Balance < command.Payload.Amount)
                throw new InsufficientFundsException($"Insufficient funds in account {account.Id}");

            // Process transaction
            account.Balance -= command.Payload.Amount;
            await repository.Persist(account);
            await Raise(new MoneyWithdrawn(account));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process withdrawal for account {AccountId}",
                command.Payload.Id);

            // Publish failure event if needed
            await Raise(new WithdrawalFailed(new WithdrawalFailureDetails
            {
                AccountId = command.Payload.Id,
                Amount = command.Payload.Amount,
                Reason = ex.Message
            }));

            throw;
        }
    }
}
```

### 6. Database Migrations

```csharp
// ✅ Good: Use ApplyMigrations for dynamic types
entityContext.Database.EnsureCreated();
entityContext.ApplyMigrations(); // Creates tables for registered types

viewModelContext.Database.EnsureCreated();
viewModelContext.ApplyMigrations(); // Creates tables for view models

// For production: Use EF Core migrations for static schema
// dotnet ef migrations add InitialCreate
// dotnet ef database update
```

### 7. Production Monitoring

```csharp
// ✅ Good: Enable observability in production
services.AddSourceFlowTelemetry(options =>
{
    options.Enabled = true;
    options.ServiceName = "ProductionApp";
    options.ServiceVersion = Assembly.GetExecutingAssembly()
        .GetName().Version.ToString();
});

// Configure appropriate sampling rate
services.AddOpenTelemetry()
    .AddSourceFlowOtlpExporter(otlpEndpoint)
    .AddSourceFlowResourceAttributes(
        ("environment", Environment.GetEnvironmentVariable("ENVIRONMENT"))
    );

// ❌ Bad: No observability in production
services.UseSourceFlow();
// Can't diagnose performance issues or failures
```

### 8. Resilience Configuration

```csharp
// ✅ Good: Enable resilience for production databases
services.AddSourceFlowEfStores(options =>
{
    options.DefaultConnectionString = connectionString;
    options.Resilience.Enabled = true;
    options.Resilience.Retry.MaxRetryAttempts = 3;
    options.Resilience.CircuitBreaker.Enabled = true;
});

// ❌ Bad: No resilience (will fail on transient errors)
services.AddSourceFlowEfStores(connectionString);
```

### 9. Command Serialization Requirements

```csharp
// ✅ Good: Command with parameterless constructor
public class ProcessPayment : Command<PaymentPayload>
{
    // REQUIRED: Parameterless constructor for deserialization
    public ProcessPayment() : base() { }

    public ProcessPayment(PaymentPayload payload) : base(payload) { }
}

// ❌ Bad: Missing parameterless constructor
public class ProcessPayment : Command<PaymentPayload>
{
    public ProcessPayment(PaymentPayload payload) : base(payload) { }
    // Will throw MissingMethodException during command replay!
}

// ✅ Good: Payload classes don't need parameterless constructors
public class PaymentPayload : IPayload
{
    public int Id { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; }
}
```

**Important Notes:**
- **Commands MUST have a public parameterless constructor** for deserialization from the command store
- Payload classes use property setters for deserialization (no parameterless constructor required)
- Without a parameterless constructor, command replay and aggregate reconstruction will fail
- The Entity Framework CommandStoreAdapter uses reflection to recreate command instances

---

## FAQ

### Q: How does SourceFlow.Net handle persistence?

**A:** SourceFlow.Net uses a store abstraction pattern with multiple implementation options:

- **In-Memory Stores**: Built-in for testing and prototyping
- **Entity Framework Stores**: Production-ready with support for SQL Server, PostgreSQL, SQLite, etc.
- **Custom Stores**: Implement `ICommandStore`, `IEntityStore`, and `IViewModelStore` for your own persistence

### Q: Can I use different databases for commands, entities, and view models?

**A:** Yes! The Entity Framework integration supports separate databases:

```csharp
services.AddSourceFlowEfStoresWithCustomProviders(
    commandContextConfig: options => options.UseSqlServer("..."),
    entityContextConfig: options => options.UsePostgreSql("..."),
    viewModelContextConfig: options => options.UseSqlite("...")
);
```

### Q: How do I handle dynamic entity and view model types?

**A:** Use the type registration and migration system:

1. Register types before building the service provider
2. Call `EnsureCreated()` to create the base schema
3. Call `ApplyMigrations()` to create tables for registered types

```csharp
EntityDbContext.RegisterEntityType<MyEntity>();
ViewModelDbContext.RegisterViewModelType<MyViewModel>();

// Build service provider...

entityContext.Database.EnsureCreated();
entityContext.ApplyMigrations();
```

### Q: Why do my commands need a parameterless constructor?

**A:** The CommandStoreAdapter uses reflection to deserialize commands from the database. When replaying commands, it needs to create instances without knowing the payload in advance.

**Required pattern:**

```csharp
public class CreateAccount : Command<CreateAccountPayload>
{
    // Required for deserialization
    public CreateAccount() : base() { }

    // Used for creating new commands
    public CreateAccount(CreateAccountPayload payload) : base(payload) { }
}
```

Without the parameterless constructor, command replay will fail with a `MissingMethodException`.

### Q: What database providers are supported?

**A:** SourceFlow.Stores.EntityFramework supports any EF Core provider:

- SQL Server (default)
- PostgreSQL (via Npgsql.EntityFrameworkCore.PostgreSQL)
- SQLite (via Microsoft.EntityFrameworkCore.Sqlite)
- MySQL (via Pomelo.EntityFrameworkCore.MySql)
- In-Memory (via Microsoft.EntityFrameworkCore.InMemoryDatabase)
- And more...

### Q: How do I test with SourceFlow.Net?

**A:** Use in-memory databases for fast, isolated tests:

```csharp
[SetUp]
public void Setup()
{
    EntityDbContext.RegisterEntityType<TestEntity>();
    ViewModelDbContext.RegisterViewModelType<TestViewModel>();

    var connection = new SqliteConnection("DataSource=:memory:");
    connection.Open();

    services.AddSourceFlowEfStoresWithCustomProvider(options =>
        options.UseSqlite(connection));

    // Build and setup...
}
```

### Q: Why use the "T" prefix for table names?

**A:** The "T" prefix distinguishes dynamically created tables from EF Core's built-in tables, making it clear which tables are part of your domain model versus infrastructure.

### Q: Should I enable observability in production?

**A:** Yes! Observability has minimal overhead (<1ms latency, <2% CPU) and provides invaluable insights:
- Distributed tracing helps debug issues across services
- Metrics help identify performance bottlenecks
- Sampling (10%) provides good coverage with minimal cost

```csharp
services.AddSourceFlowTelemetry(options =>
{
    options.Enabled = true;
    options.ServiceName = "ProductionApp";
});
services.AddOpenTelemetry()
    .AddSourceFlowOtlpExporter(otlpEndpoint);
```

### Q: When should I use resilience patterns?

**A:** Always enable resilience in production for database operations:
- Retry policies handle transient network failures
- Circuit breakers prevent cascading failures
- Timeouts prevent hanging operations

```csharp
services.AddSourceFlowEfStores(options =>
{
    options.Resilience.Enabled = true;
});
```

### Q: How much does ArrayPool improve performance?

**A:** In high-throughput scenarios (>1000 commands/second):
- **Memory**: 40x reduction in allocations (40MB → <1MB for 10K commands)
- **GC**: 70% reduction in Gen0 collections
- **Throughput**: 25-40% improvement
- **Zero configuration**: Works automatically once enabled

ArrayPool optimizations are built-in and automatically applied to command serialization and event dispatching.

### Q: How do I handle schema changes?

**A:** For production applications:

1. Use EF Core migrations for base schema
2. Use `ApplyMigrations()` for dynamic types
3. Version your entities and view models
4. Implement upcasting for old events

For development/testing:

1. Use `Database.EnsureDeleted()` and `EnsureCreated()`
2. Use in-memory databases that reset on each test

### Q: Can I use SourceFlow.EntityFramework with MySQL or other databases?

**A:** Yes! Use `AddSourceFlowEfStoresWithCustomProvider` for any EF Core supported database:

```csharp
// MySQL
var serverVersion = new MySqlServerVersion(new Version(8, 0, 21));
services.AddSourceFlowEfStoresWithCustomProvider(options =>
    options.UseMySql(connectionString, serverVersion));

// SQLite
services.AddSourceFlowEfStoresWithCustomProvider(options =>
    options.UseSqlite("Data Source=sourceflow.db"));

// PostgreSQL
services.AddSourceFlowEfStoresWithCustomProvider(options =>
    options.UseNpgsql(connectionString));
```

The `AddSourceFlowEfStores` methods without "CustomProvider" use SQL Server by default.

### Q: What's the difference between EnsureCreated() and ApplyMigrations()?

**A:**
- `EnsureCreated()`: Creates the database and base schema (Commands, fixed tables)
- `ApplyMigrations()`: Creates tables for dynamically registered entities and view models

Always call both in the correct order:

```csharp
await entityContext.Database.EnsureCreatedAsync();  // Create database
entityContext.ApplyMigrations();                     // Create dynamic tables
```

### Q: How do I configure EF Core 9.0 for testing to avoid provider conflicts?

**A:** When testing with multiple DbContext providers (e.g., SQLite for tests, SQL Server for production), use `EnableServiceProviderCaching(false)`:

```csharp
services.AddDbContext<EntityDbContext>(options =>
    options.UseSqlite(connection)
        .EnableServiceProviderCaching(false));  // Required for EF Core 9.0
```

This prevents the "multiple provider" error when using different providers in the same service collection.

### Q: Should I register stores manually or use AddSourceFlowEfStores?

**A:** Use `AddSourceFlowEfStores` for production. Only register manually for special cases like:

- Testing scenarios requiring specific service configuration
- Custom implementations of resilience or telemetry services
- Avoiding provider conflicts in test setups

Example manual registration:

```csharp
var efOptions = new SourceFlowEfOptions();
services.AddSingleton(efOptions);
services.AddScoped<IDatabaseResiliencePolicy, DatabaseResiliencePolicy>();
services.AddScoped<IDatabaseTelemetryService, DatabaseTelemetryService>();
services.AddScoped<ICommandStore, EfCommandStore>();
services.AddScoped<IEntityStore, EfEntityStore>();
services.AddScoped<IViewModelStore, EfViewModelStore>();
```

### Q: How do table naming conventions affect my database schema?

**A:** Table naming conventions transform entity type names into table names:

```csharp
// Default (PascalCase, no prefix/suffix)
BankAccount → BankAccount

// Snake case with pluralization
options.EntityTableNaming.Casing = TableNameCasing.SnakeCase;
options.EntityTableNaming.Pluralize = true;
BankAccount → bank_accounts

// With schema
options.EntityTableNaming.UseSchema = true;
options.EntityTableNaming.SchemaName = "domain";
BankAccount → domain.BankAccount

// Combined
BankAccount → domain.bank_accounts
```

Set naming conventions BEFORE calling `ApplyMigrations()` to ensure tables are created with the correct names.

---

## Production Considerations

### Performance Optimization

1. **Use Separate Databases**: Split command, entity, and view model stores across different databases
2. **Enable Connection Pooling**: Configure appropriate connection pool sizes
3. **Optimize Queries**: Use AsNoTracking() for read-only queries
4. **Batch Operations**: Use bulk insert/update operations where applicable
5. **Enable ArrayPool**: Automatically enabled for high-throughput scenarios (40x reduction in allocations)
6. **Configure Observability**: Use appropriate sampling rates for production (1-10%)
7. **Enable Resilience**: Use Polly policies for fault tolerance in production

### Monitoring

```csharp
// Health checks
services.AddHealthChecks()
    .AddDbContextCheck<CommandDbContext>("commandstore")
    .AddDbContextCheck<EntityDbContext>("entitystore")
    .AddDbContextCheck<ViewModelDbContext>("viewmodelstore");

// OpenTelemetry metrics and tracing
services.AddSourceFlowTelemetry("ProductionApp", "1.0.0");
services.AddOpenTelemetry()
    .AddSourceFlowOtlpExporter("http://localhost:4317");

// Monitor key metrics:
// - Command throughput: sourceflow.domain.commands.executed
// - Operation latency: sourceflow.domain.operation.duration (P50/P95/P99)
// - Circuit breaker state: polly.circuit_breaker.state
// - GC pressure: dotnet.gc.collections (reduced with ArrayPool)
```

### Deployment

1. **Migrations**: Apply EF Core migrations during deployment
2. **Connection Strings**: Use environment-specific configuration
3. **Logging**: Configure appropriate logging levels
4. **Error Handling**: Implement global exception handling

### Common Issues and Solutions

**Command Deserialization Failures:**
- **Symptom**: `MissingMethodException` or `InvalidOperationException` during command replay
- **Cause**: Command class missing parameterless constructor
- **Solution**: Add public parameterless constructor to all command classes

```csharp
// Fix: Add parameterless constructor
public class MyCommand : Command<MyPayload>
{
    public MyCommand() : base() { }  // Required!
    public MyCommand(MyPayload payload) : base(payload) { }
}
```

**Entity Tracking Conflicts:**
- **Symptom**: "Instance already being tracked" errors
- **Cause**: Multiple entity instances with same ID in EF change tracker
- **Solution**: Use `AsNoTracking()` for read operations or detach entities after save

**EF Core 9.0 Provider Conflicts (Testing):**
- **Symptom**: "An error occurred accessing the database provider factory"
- **Cause**: Multiple providers registered in same service collection
- **Solution**: Use `EnableServiceProviderCaching(false)` in test configurations

**Migration Failures:**
- **Symptom**: Tables not created for entities or view models
- **Cause**: Types not registered before calling `ApplyMigrations()`
- **Solution**: Register types using `EntityDbContext.RegisterAssembly()` before building service provider

---

## Community and Support

### Resources

- **GitHub Repository**: [https://github.com/CodeShayk/SourceFlow.Net](https://github.com/CodeShayk/SourceFlow.Net)
- **Documentation**: [https://github.com/CodeShayk/SourceFlow.Net/wiki](https://github.com/CodeShayk/SourceFlow.Net/wiki)
- **Issues**: [https://github.com/CodeShayk/SourceFlow.Net/issues](https://github.com/CodeShayk/SourceFlow.Net/issues)
- **Discussions**: [https://github.com/CodeShayk/SourceFlow.Net/discussions](https://github.com/CodeShayk/SourceFlow.Net/discussions)

### License

SourceFlow.Net is released under the MIT License, making it free for both commercial and open-source use.

---

## Conclusion

SourceFlow.Net provides a robust, scalable foundation for building event-sourced applications with .NET. By combining Event Sourcing, Domain-Driven Design, and CQRS patterns with flexible Entity Framework persistence, it enables developers to create maintainable, auditable, and performant systems.

**Start your journey with SourceFlow.Net today and build better software with events as your foundation!**
