# SourceFlow.Net - Complete Guide

## Table of Contents
1. [Introduction](#introduction)
2. [Core Concepts](#core-concepts)
3. [Architecture Overview](#architecture-overview)
4. [Getting Started](#getting-started)
5. [Framework Components](#framework-components)
6. [Persistence with Entity Framework](#persistence-with-entity-framework)
7. [Implementation Guide](#implementation-guide)
8. [Advanced Features](#advanced-features)
9. [Best Practices](#best-practices)
10. [FAQ](#faq)

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

```
┌─────────────────────┐    ┌─────────────────────┐    ┌─────────────────────┐
│     Aggregate       │    │        Sagas        │    │    Projections      │
│                     │    │                     │    │                     │
│ ┌─────────────────┐ │    │ ┌─────────────────┐ │    │ ┌─────────────────┐ │
│ │ AccountAggregate│ │    │ │ AccountSaga     │ │    │ │   AccountView   │ │
│ └─────────────────┘ │    │ └─────────────────┘ │    │ └─────────────────┘ │
└─────────────────────┘    └─────────────────────┘    └─────────────────────┘
           │                       │                            │
        Commands               Commands                    ViewData
           ▼                       ▼                            ▼
┌─────────────────────┐    ┌─────────────────────┐    ┌─────────────────────┐
│   Command Bus       │    │     Event Queue     │    │   ViewModel Store   │
│  ┌───────────────┐  │    │  ┌──────────────┐  │    │  ┌──────────────┐   │
│  │CommandPublisher│ │    │  │EventDispatcher│ │    │  │ EfViewStore  │   │
│  └───────────────┘  │    │  └──────────────┘  │    │  └──────────────┘   │
└─────────────────────┘    └─────────────────────┘    └─────────────────────┘
           │                       │
           │                    Events
           ▼                       │
┌─────────────────────┐           │
│   Command Store     │           │
│  ┌──────────────┐   │           │
│  │EfCommandStore│   │           │
│  └──────────────┘   │           │
└─────────────────────┘           │
           │                       │
           │                       │
           ▼                       ▼
┌─────────────────────────────────────────────────────────────┐
│              Entity Framework Core DbContexts                │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────────┐  │
│  │CommandDbCtx  │  │ EntityDbCtx  │  │ViewModelDbCtx    │  │
│  └──────────────┘  └──────────────┘  └──────────────────┘  │
│           │               │                   │              │
│           ▼               ▼                   ▼              │
│    ┌─────────────────────────────────────────────┐          │
│    │      SQL Server / PostgreSQL / SQLite       │          │
│    └─────────────────────────────────────────────┘          │
└─────────────────────────────────────────────────────────────┘
```

### Component Interactions

1. **Aggregates** encapsulate business logic and send commands
2. **Command Bus** routes commands to appropriate saga handlers
3. **Sagas** handle commands and maintain consistency across aggregates
4. **Sagas** persist entities to the **Entity Store**
5. **Sagas** raise events to the **Event Queue**
6. **Event Queue** dispatches events to subscribers
7. **Projections** update read models (ViewModels) based on events
8. **Command Store** persists commands for replay capability
9. **Entity Framework Stores** provide persistence using EF Core with support for multiple databases

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
// Program.cs or Startup.cs
using SourceFlow;
using SourceFlow.Stores.EntityFramework.Extensions;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();

// Add logging
services.AddLogging(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});

// Configure SourceFlow with automatic discovery
services.UseSourceFlow(Assembly.GetExecutingAssembly());

// Add Entity Framework stores with a single connection string
services.AddSourceFlowEfStores("Server=localhost;Database=SourceFlow;Integrated Security=true;");

var serviceProvider = services.BuildServiceProvider();
```

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
services.UseSourceFlow(Assembly.GetExecutingAssembly());

// Add Entity Framework stores
services.AddSourceFlowEfStoresWithCustomProvider(options =>
    options.UseSqlServer("Server=localhost;Database=SourceFlow;Integrated Security=true;"));

var serviceProvider = services.BuildServiceProvider();

// Ensure all databases are created and migrated
var commandContext = serviceProvider.GetRequiredService<CommandDbContext>();
commandContext.Database.EnsureCreated();

var entityContext = serviceProvider.GetRequiredService<EntityDbContext>();
entityContext.Database.EnsureCreated();
entityContext.ApplyMigrations(); // Create tables for registered entity types

var viewModelContext = serviceProvider.GetRequiredService<ViewModelDbContext>();
viewModelContext.Database.EnsureCreated();
viewModelContext.ApplyMigrations(); // Create tables for registered view model types
```

### Testing with In-Memory Database

```csharp
[SetUp]
public void Setup()
{
    // Clear previous registrations
    EntityDbContext.ClearRegistrations();
    ViewModelDbContext.ClearRegistrations();

    // Register test types
    EntityDbContext.RegisterEntityType<TestEntity>();
    ViewModelDbContext.RegisterViewModelType<TestViewModel>();

    // Create shared in-memory connection
    var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
    connection.Open();

    var services = new ServiceCollection();

    // Configure with in-memory SQLite
    services.AddDbContext<CommandDbContext>(options =>
        options.UseSqlite(connection));
    services.AddDbContext<EntityDbContext>(options =>
        options.UseSqlite(connection));
    services.AddDbContext<ViewModelDbContext>(options =>
        options.UseSqlite(connection));

    services.AddSourceFlowEfStores("DataSource=:memory:");

    var serviceProvider = services.BuildServiceProvider();

    // Create all schemas
    var commandContext = serviceProvider.GetRequiredService<CommandDbContext>();
    commandContext.Database.EnsureCreated();

    var entityContext = serviceProvider.GetRequiredService<EntityDbContext>();
    entityContext.Database.EnsureCreated();
    entityContext.ApplyMigrations();

    var viewModelContext = serviceProvider.GetRequiredService<ViewModelDbContext>();
    viewModelContext.Database.EnsureCreated();
    viewModelContext.ApplyMigrations();
}
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
    public CreateAccount(CreateAccountPayload payload) : base(payload) { }
}

public class DepositMoney : Command<TransactionPayload>
{
    public DepositMoney(TransactionPayload payload) : base(payload) { }
}

public class WithdrawMoney : Command<TransactionPayload>
{
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

// Register entity and view model types
EntityDbContext.RegisterEntityType<BankAccount>();
ViewModelDbContext.RegisterViewModelType<AccountViewModel>();

// Configure SourceFlow
services.UseSourceFlow(Assembly.GetExecutingAssembly());

// Add Entity Framework stores
services.AddSourceFlowEfStores("Server=localhost;Database=SourceFlow;Integrated Security=true;");

var serviceProvider = services.BuildServiceProvider();

// Ensure databases are created
var commandContext = serviceProvider.GetRequiredService<CommandDbContext>();
commandContext.Database.EnsureCreated();

var entityContext = serviceProvider.GetRequiredService<EntityDbContext>();
entityContext.Database.EnsureCreated();
entityContext.ApplyMigrations();

var viewModelContext = serviceProvider.GetRequiredService<ViewModelDbContext>();
viewModelContext.Database.EnsureCreated();
viewModelContext.ApplyMigrations();

// Use the aggregate
var aggregateFactory = serviceProvider.GetRequiredService<IAggregateFactory>();
var accountAggregate = await aggregateFactory.Create<IAccountAggregate>();

accountAggregate.CreateAccount(999, "John Doe", 1000m);
accountAggregate.Deposit(999, 500m);
accountAggregate.Withdraw(999, 200m);

// Query the read model
var viewModelStore = serviceProvider.GetRequiredService<IViewModelStoreAdapter>();
var accountView = await viewModelStore.Find<AccountViewModel>(999);

Console.WriteLine($"Account: {accountView.AccountName}");
Console.WriteLine($"Balance: {accountView.CurrentBalance:C}");
Console.WriteLine($"Transactions: {accountView.TransactionCount}");
```

---

## Advanced Features

### Event Replay

SourceFlow.Net provides built-in command replay functionality for debugging and state reconstruction:

```csharp
// Load all commands for an aggregate
var commandStore = serviceProvider.GetRequiredService<ICommandStore>();
var commands = await commandStore.Load(aggregateId);

foreach (var command in commands)
{
    Console.WriteLine($"Command: {command.GetType().Name}");
    Console.WriteLine($"Timestamp: {command.Metadata.OccurredOn}");
    Console.WriteLine($"Sequence: {command.Metadata.SequenceNo}");
}
```

### Metadata and Auditing

Every command and event includes rich metadata:

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

## Best Practices

### 1. Command Design

```csharp
// ✅ Good: Specific, intention-revealing commands
public class WithdrawMoney : Command<WithdrawPayload> { }
public class DepositMoney : Command<DepositPayload> { }

// ❌ Bad: Generic, unclear commands
public class UpdateAccount : Command<AccountPayload> { }
```

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

### Q: What database providers are supported?

**A:** SourceFlow.Stores.EntityFramework supports any EF Core provider:

- SQL Server (default)
- PostgreSQL (via Npgsql.EntityFrameworkCore.PostgreSQL)
- SQLite (via Microsoft.EntityFrameworkCore.Sqlite)
- MySQL (via Pomelo.EntityFrameworkCore.MySql)
- In-Memory (via Microsoft.EntityFrameworkCore.InMemory)
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

### Q: How do I handle schema changes?

**A:** For production applications:

1. Use EF Core migrations for base schema
2. Use `ApplyMigrations()` for dynamic types
3. Version your entities and view models
4. Implement upcasting for old events

For development/testing:

1. Use `Database.EnsureDeleted()` and `EnsureCreated()`
2. Use in-memory databases that reset on each test

---

## Production Considerations

### Performance Optimization

1. **Use Separate Databases**: Split command, entity, and view model stores across different databases
2. **Enable Connection Pooling**: Configure appropriate connection pool sizes
3. **Optimize Queries**: Use AsNoTracking() for read-only queries
4. **Batch Operations**: Use bulk insert/update operations where applicable

### Monitoring

```csharp
services.AddHealthChecks()
    .AddDbContextCheck<CommandDbContext>("commandstore")
    .AddDbContextCheck<EntityDbContext>("entitystore")
    .AddDbContextCheck<ViewModelDbContext>("viewmodelstore");
```

### Deployment

1. **Migrations**: Apply EF Core migrations during deployment
2. **Connection Strings**: Use environment-specific configuration
3. **Logging**: Configure appropriate logging levels
4. **Error Handling**: Implement global exception handling

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
