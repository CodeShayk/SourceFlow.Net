# SourceFlow.Net

A modern, lightweight, and extensible .NET framework for building event-sourced applications using Domain-Driven Design (DDD) principles and Command Query Responsibility Segregation (CQRS) patterns.
> Build scalable, maintainable applications with complete event sourcing, aggregate pattern implementation, saga orchestration for long-running transactions, and view model projections.

---

## 🚀 Overview

SourceFlow.Net is a comprehensive event sourcing and CQRS framework that empowers developers to build scalable, maintainable applications with complete audit trails. Built from the ground up for modern .NET development with performance and developer experience as core priorities.

### Key Features

- 🏗️ **Domain-Driven Design (DDD)** - Complete support for domain modeling and bounded contexts
- ⚡ **CQRS Implementation** - Command/Query separation for optimized read and write operations
- 📊 **Event-First Design** - Foundation built on event sourcing with complete audit trails
- 🧱 **Clean Architecture** - Separation of concerns with clear architectural boundaries
- 🔒 **Resilience Ready** - Built-in retry policies and circuit breakers
- 📈 **Observability** - Integrated OpenTelemetry support for monitoring and tracing
- 🔧 **Extensible** - Pluggable persistence and messaging layers

### 🎯 Core Architecture

SourceFlow.Net implements the following architectural patterns:

#### **Aggregates** (Dual Role: Command Publisher & Event Subscriber)
- Encapsulate root domain entities within bounded contexts
- Command Publisher: Provide the API for publishing commands to initiate state changes
- Event Subscriber: Subscribe to events to react to external changes from other sagas or workflows
- Manage consistency boundaries for domain invariants
- Unique in their dual responsibility of both publishing commands and subscribing to events

#### **Sagas**
- Command Subscriber: Subscribe to commands and execute updates to aggregate entities
- Orchestrate long-running business processes and transactions
- Manage both success and failure flows to ensure data consistency
- Publish commands to themselves or other sagas to coordinate multi-step workflows
- Raise events during command handling to notify other components of state changes

#### **Events**
- Immutable notifications of state changes that have occurred
- Published to interested subscribers when state changes occur
- Two primary subscribers:
  - **Aggregates**: React to events from external workflows that impact their domain state
  - **Views**: Project event data into optimized read models for query operations

#### **Views & ViewModels**
- Event Subscriber: Subscribe to events and transform domain data into denormalized read models
- Provide optimized read access for consumers such as UIs or reporting systems
- Support eventual consistency patterns for high-performance queries

---

## 📦 Installation

Install the core SourceFlow.Net package using NuGet Package Manager:

```bash
# Core framework
dotnet add package SourceFlow.Net

# Entity Framework persistence (optional but recommended)
dotnet add package SourceFlow.Stores.EntityFramework
```

### .NET Framework Support
- .NET Framework 4.6.2
- .NET Standard 2.0 / 2.1
- .NET 9.0 / 10.0

---

## 🛠️ Quick Start Guide

This comprehensive example demonstrates a complete banking system implementation with deposits, withdrawals, and account management.

### 1. Define Your Domain Entity

```csharp
using SourceFlow;

public class BankAccount : IEntity
{
    public int Id { get; set; }
    public decimal Balance { get; set; }
    public string AccountHolder { get; set; }
    public string AccountNumber { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedDate { get; set; }
}
```

### 2. Create Commands with Payloads

```csharp
using SourceFlow.Messaging.Commands;

// Create account command
public class CreateAccountCommand : Command<CreateAccountPayload>
{
    public CreateAccountCommand() { } // Default constructor for serialization

    public CreateAccountCommand(CreateAccountPayload payload)
        : base(true, payload) { }
}

public class CreateAccountPayload : IPayload
{
    public CreateAccountPayload() { } // Default constructor for serialization

    public string AccountHolder { get; set; }
    public string AccountNumber { get; set; }
    public decimal InitialDeposit { get; set; }
}

// Deposit command
public class DepositCommand : Command<DepositPayload>
{
    public DepositCommand() { } // Default constructor for serialization

    public DepositCommand(int accountId, DepositPayload payload)
        : base(accountId, payload) { }
}

public class DepositPayload : IPayload
{
    public DepositPayload() { } // Default constructor for serialization

    public decimal Amount { get; set; }
    public string TransactionReference { get; set; }
}

// Withdraw command
public class WithdrawCommand : Command<WithdrawPayload>
{
    public WithdrawCommand() { } // Default constructor for serialization

    public WithdrawCommand(int accountId, WithdrawPayload payload)
        : base(accountId, payload) { }
}

public class WithdrawPayload : IPayload
{
    public WithdrawPayload() { } // Default constructor for serialization

    public decimal Amount { get; set; }
    public string TransactionReference { get; set; }
}

// Close account command
public class CloseAccountCommand : Command<CloseAccountPayload>
{
    public CloseAccountCommand() { } // Default constructor for serialization

    public CloseAccountCommand(int accountId, CloseAccountPayload payload)
        : base(accountId, payload) { }
}

public class CloseAccountPayload : IPayload
{
    public CloseAccountPayload() { } // Default constructor for serialization

    public string Reason { get; set; }
}
```

### 3. Implement a Saga with Command Handling

Sagas handle commands, apply business logic, and optionally raise events. Note that entity operations now return the persisted entity for additional processing.

```csharp
using SourceFlow.Saga;
using SourceFlow.Messaging.Events;
using Microsoft.Extensions.Logging;

public class BankAccountSaga : Saga<BankAccount>,
    IHandles<CreateAccountCommand>, // Handles command only
    IHandlesWithEvent<DepositCommand, AccountDepositedEvent>, // Handles command and publishes event at the end.
    IHandlesWithEvent<WithdrawCommand, AccountWithdrewEvent>,
    IHandlesWithEvent<CloseAccountCommand, AccountClosedEvent>
{
    public BankAccountSaga(
        Lazy<ICommandPublisher> commandPublisher,
        IEventQueue eventQueue,
        IEntityStoreAdapter entityStore,
        ILogger<ISaga> logger)
        : base(commandPublisher, eventQueue, entityStore, logger)
    {
    }

    public async Task<IEntity> Handle(IEntity entity, CreateAccountCommand command)
    {
        var account = (BankAccount)entity;
        account.Id = command.Entity.Id; // Use the auto-generated ID
        account.AccountHolder = command.Payload.AccountHolder;
        account.AccountNumber = command.Payload.AccountNumber;
        account.Balance = command.Payload.InitialDeposit;
        account.IsActive = true;
        account.CreatedDate = DateTime.UtcNow;

        return account;
    }

    public async Task<IEntity> Handle(IEntity entity, DepositCommand command)
    {
        var account = (BankAccount)entity;

        if (!account.IsActive)
            throw new InvalidOperationException("Cannot deposit to inactive account");

        if (command.Payload.Amount <= 0)
            throw new ArgumentException("Deposit amount must be positive");

        account.Balance += command.Payload.Amount;
        return account;
    }

    public async Task<IEntity> Handle(IEntity entity, WithdrawCommand command)
    {
        var account = (BankAccount)entity;

        if (!account.IsActive)
            throw new InvalidOperationException("Cannot withdraw from inactive account");

        if (command.Payload.Amount <= 0)
            throw new ArgumentException("Withdrawal amount must be positive");

        if (account.Balance < command.Payload.Amount)
            throw new InvalidOperationException("Insufficient funds");

        account.Balance -= command.Payload.Amount;
        return account;
    }

    public async Task<IEntity> Handle(IEntity entity, CloseAccountCommand command)
    {
        var account = (BankAccount)entity;
        account.IsActive = false;
        return account;
    }
}
```

### 4. Create Domain Events

Events notify other parts of the system when state changes occur.

```csharp
using SourceFlow.Messaging.Events;

public class AccountDepositedEvent : Event<BankAccount>
{
    public AccountDepositedEvent(BankAccount account) : base(account) { }
}

public class AccountWithdrewEvent : Event<BankAccount>
{
    public AccountWithdrewEvent(BankAccount account) : base(account) { }
}

public class AccountClosedEvent : Event<BankAccount>
{
    public AccountClosedEvent(BankAccount account) : base(account) { }
}
```

### 5. Define View Models for Read Operations

```csharp
using SourceFlow.Projections;

public class AccountSummaryViewModel : IViewModel
{
    public int Id { get; set; }
    public string AccountHolder { get; set; }
    public string AccountNumber { get; set; }
    public decimal Balance { get; set; }
    public bool IsActive { get; set; }
    public DateTime LastUpdated { get; set; }
}

public class TransactionHistoryViewModel : IViewModel
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public string TransactionType { get; set; }
    public decimal Amount { get; set; }
    public decimal NewBalance { get; set; }
    public string Reference { get; set; }
    public DateTime Timestamp { get; set; }
}
```

### 6. Implement Views for Event Projections

**Enhanced Feature: Store operations now return the persisted entity**, which can be useful when the store modifies the entity (e.g., sets database-generated IDs or updates timestamps). Views serve as **Event Subscribers** that project events into view models for efficient querying.

```csharp
using SourceFlow.Projections;
using Microsoft.Extensions.Logging;

public class AccountSummaryView : View<AccountSummaryViewModel>,
    IProjectOn<AccountDepositedEvent>,   // Event Subscriber: Subscribes to AccountDepositedEvent
    IProjectOn<AccountWithdrewEvent>,    // Event Subscriber: Subscribes to AccountWithdrewEvent
    IProjectOn<AccountClosedEvent>       // Event Subscriber: Subscribes to AccountClosedEvent
{
    public AccountSummaryView(
        IViewModelStoreAdapter viewModelStore,
        ILogger<IView> logger)
        : base(viewModelStore, logger)
    {
    }

    // Event Subscriber: Reacts to AccountDepositedEvent by updating AccountSummaryViewModel
    public async Task<AccountSummaryViewModel> On(AccountDepositedEvent @event)
    {
        var account = @event.Payload;

        // Check if view model already exists, otherwise create new one
        var viewModel = await Find<AccountSummaryViewModel>(account.Id) ?? new AccountSummaryViewModel { Id = account.Id };

        viewModel.AccountHolder = account.AccountHolder;
        viewModel.AccountNumber = account.AccountNumber;
        viewModel.Balance = account.Balance;
        viewModel.IsActive = account.IsActive;
        viewModel.LastUpdated = DateTime.UtcNow;

        return viewModel;
    }

    // Event Subscriber: Reacts to AccountWithdrewEvent by updating AccountSummaryViewModel
    public async Task<AccountSummaryViewModel> On(AccountWithdrewEvent @event)
    {
        var account = @event.Payload;

        // Find existing view model
        var viewModel = await Find<AccountSummaryViewModel>(account.Id) ?? new AccountSummaryViewModel { Id = account.Id };

        viewModel.AccountHolder = account.AccountHolder;
        viewModel.AccountNumber = account.AccountNumber;
        viewModel.Balance = account.Balance;
        viewModel.IsActive = account.IsActive;
        viewModel.LastUpdated = DateTime.UtcNow;

        return viewModel;
    }

    // Event Subscriber: Reacts to AccountClosedEvent by updating AccountSummaryViewModel
    public async Task<AccountSummaryViewModel> On(AccountClosedEvent @event)
    {
        var account = @event.Payload;

        // Find existing view model
        var viewModel = await Find<AccountSummaryViewModel>(account.Id) ?? new AccountSummaryViewModel { Id = account.Id };

        viewModel.AccountHolder = account.AccountHolder;
        viewModel.AccountNumber = account.AccountNumber;
        viewModel.Balance = account.Balance;
        viewModel.IsActive = false; // Always set to inactive when closed
        viewModel.LastUpdated = DateTime.UtcNow;

        return viewModel;
    }
}

public class TransactionHistoryView : View<TransactionHistoryViewModel>,
    IProjectOn<AccountDepositedEvent>,  // Event Subscriber: Subscribes to AccountDepositedEvent
    IProjectOn<AccountWithdrewEvent>     // Event Subscriber: Subscribes to AccountWithdrewEvent
{
    public TransactionHistoryView(
        IViewModelStoreAdapter viewModelStore,
        ILogger<IView> logger)
        : base(viewModelStore, logger)
    {
    }

    // Event Subscriber: Reacts to AccountDepositedEvent by creating TransactionHistoryViewModel
    public async Task<TransactionHistoryViewModel> On(AccountDepositedEvent @event)
    {
        var account = @event.Payload;
        var transaction = new TransactionHistoryViewModel
        {
            AccountId = account.Id,
            TransactionType = "Deposit",
            Amount = Math.Abs(account.Balance - (account.Balance - @event.Payload.Balance)), // Calculate the deposit amount
            NewBalance = account.Balance,
            Reference = "DEP-" + DateTime.UtcNow.Ticks,
            Timestamp = DateTime.UtcNow
        };

        return transaction;
    }

    // Event Subscriber: Reacts to AccountWithdrewEvent by creating TransactionHistoryViewModel
    public async Task<TransactionHistoryViewModel> On(AccountWithdrewEvent @event)
    {
        var account = @event.Payload;
        var transaction = new TransactionHistoryViewModel
        {
            AccountId = account.Id,
            TransactionType = "Withdrawal",
            Amount = Math.Abs(account.Balance - (account.Balance + @event.Payload.Balance)), // Calculate the withdrawal amount
            NewBalance = account.Balance,
            Reference = "WD-" + DateTime.UtcNow.Ticks,
            Timestamp = DateTime.UtcNow
        };
       
        return transaction;
    }
}
```

### 7. Create an Aggregate Root

Aggregates serve as both **Command Publishers** and **Event Subscribers**, managing entities within a bounded context and providing the public API for command publishing while reacting to relevant events.

```csharp
using SourceFlow.Aggregate;
using Microsoft.Extensions.Logging;

public class BankAccountAggregate : Aggregate<BankAccount>, IBankAccountAggregate
    ISubscribes<AccountDepositedEvent>,  // Event Subscriber: Subscribes to AccountDepositedEvent
    ISubscribes<AccountWithdrewEvent>    // Event Subscriber: Subscribes to AccountWithdrewEvent
{
    public BankAccountAggregate(
        Lazy<ICommandPublisher> commandPublisher,  // Command Publisher: Used to publish commands
        IAggregateFactory aggregateFactory,
        ILogger<IAggregate> logger)
        : base(commandPublisher, logger)
    {
    }

    // Command Publisher: Public method to initiate state changes by publishing commands
    public async Task<int> CreateAccountAsync(string accountHolder, string accountNumber, decimal initialDeposit = 0)
    {
        var command = new CreateAccountCommand(new CreateAccountPayload
        {
            AccountHolder = accountHolder,
            AccountNumber = accountNumber,
            InitialDeposit = initialDeposit
        });
		
		 // Use 0 for auto-generated ID or actual ID if known, for new entity to be created.
        command.Entity = new EntityRef { Id = 0, IsNew = true }; 

        // Using Send method from Aggregate base class to publish command (Command Publisher role)
        await Send(command);

        // Return the new account ID
        return command.Entity.Id;
    }

    // Command Publisher: Public method to initiate deposit command
    public async Task DepositAsync(int accountId, decimal amount, string reference = null)
    {
        var command = new DepositCommand(accountId, new DepositPayload
        {
            Amount = amount,
            TransactionReference = reference ?? $"DEP-{DateTime.UtcNow.Ticks}"
        });

        command.Entity = new EntityRef { Id = accountId, IsNew = false };

        // Using Send method from Aggregate base class to publish command (Command Publisher role)
        await Send(command);
    }

    // Event Subscriber: Reacts to AccountDepositedEvent
    public async Task On(AccountDepositedEvent @event)
    {
        // React to events from other sagas if needed (Event Subscriber role)
        // For example, update internal state or trigger other business logic
        logger.LogInformation("Account {AccountId} received deposit event", @event.Payload.Id);
    }

    // Event Subscriber: Reacts to AccountWithdrewEvent
    public async Task On(AccountWithdrewEvent @event)
    {
        // React to withdrawal events (Event Subscriber role)
        logger.LogInformation("Account {AccountId} received withdrawal event", @event.Payload.Id);
    }
}
```

### 8. Configure Services in Startup

```csharp
public void ConfigureServices(IServiceCollection services)
{
    // Register SourceFlow with automatic discovery
    services.UseSourceFlow(Assembly.GetExecutingAssembly());

    // Configure Entity Framework persistence (optional)
    services.AddSourceFlowStores(configuration, options =>
    {
        // Option 1: Use separate connection strings for each store
        options.UseCommandStore("CommandStoreConnection");
        options.UseEntityStore("EntityStoreConnection");
        options.UseViewModelStore("ViewModelStoreConnection");

        // Option 2: Use a shared connection string
        // options.UseSharedConnectionString("DefaultConnection");
    });

    // Optional: Configure observability
    services.AddSingleton(new DomainObservabilityOptions
    {
        Enabled = true,
        ServiceName = "BankingService",
        ServiceVersion = "1.0.0"
    });
}
```

### 9. Use in Your Services

Aggregates function as the primary **Command Publishers** in your application, allowing services to initiate state changes while maintaining their role as **Event Subscribers** to react to system events. When implemented as shown above, the aggregate exposes specific business methods that handle command publication internally.

```csharp
using SourceFlow.Aggregate;

public class BankingService
{
    // The aggregate serves as both Command Publisher and Event Subscriber
    private readonly IBankAccountAggregate _aggregate;

    public BankingService(IBankAccountAggregate aggregate)
    {
        _aggregate = aggregate;
    }

    public async Task<int> CreateAccountAsync(string accountHolder, string accountNumber, decimal initialDeposit = 0)
    {
        // Delegates to the aggregate's Command Publisher method
        return await _aggregate.CreateAccountAsync(accountHolder, accountNumber, initialDeposit);
    }

    public async Task DepositAsync(int accountId, decimal amount, string reference = null)
    {
        // Delegates to the aggregate's Command Publisher method
        await _aggregate.DepositAsync(accountId, amount, reference);
    }

    public async Task WithdrawAsync(int accountId, decimal amount, string reference = null)
    {
        var command = new WithdrawCommand(accountId, new WithdrawPayload
        {
            Amount = amount,
            TransactionReference = reference ?? $"WD-{DateTime.UtcNow.Ticks}"
        });

        command.Entity = new EntityRef { Id = accountId, IsNew = false };

        // Directly publishing a command through the Aggregate (Command Publisher role)
        await _aggregate.Send(command);
    }
}
```

---

## 🏗️ Architecture Flow

![Architecture]("https://github.com/CodeShayk/SourceFlow.Net/blob/v1.0.0/Images/Architecture.png" "Architecture")

---

## ⚙️ Advanced Configuration

### Basic Setup

```csharp
// Simple registration with automatic discovery
services.UseSourceFlow();

// With specific assemblies
services.UseSourceFlow(Assembly.GetExecutingAssembly(), typeof(SomeOtherAssembly).Assembly);

// With custom service lifetime
services.UseSourceFlow(ServiceLifetime.Scoped, Assembly.GetExecutingAssembly());
```

### With Observability Enabled

```csharp
services.AddSingleton(new DomainObservabilityOptions
{
    Enabled = true,
    ServiceName = "MyService",
    ServiceVersion = "1.0.0",
    MetricsEnabled = true,
    TracingEnabled = true,
    LoggingEnabled = true
});

services.UseSourceFlow();
```

### Custom Persistence Configuration

```csharp
// Custom store implementations
services.AddSingleton<IEntityStore, CustomEntityStore>();
services.AddSingleton<ICommandStore, CustomCommandStore>();
services.AddSingleton<IViewModelStore, CustomViewModelStore>();

services.UseSourceFlow();
```

---

## 🗂️ Persistence Options

SourceFlow.Net supports pluggable persistence through store interfaces:

- `ICommandStore` - Stores command history for audit trails and replay
- `IEntityStore` - Stores current state of domain entities
- `IViewModelStore` - Stores optimized read models for queries

### Entity Framework Provider

The Entity Framework provider offers:
- SQL Server support with optimized schema
- Resilience policies with automatic retry and circuit breaker
- OpenTelemetry integration for database operations
- Configurable connection strings per store type
- **Enhanced Return Types**: Store operations return the persisted entity for additional processing

Install with:
```bash
dotnet add package SourceFlow.Stores.EntityFramework
```

### Custom Store Implementation

```csharp
public class CustomEntityStore : IEntityStore
{
    public async Task<T> Get<T>(int id) where T : IEntity
    {
        // Custom retrieval logic
    }

    public async Task<T> Persist<T>(T entity) where T : IEntity
    {
        // Custom persistence logic that returns the persisted entity
        // This allows for updates made by the store (like database-generated IDs)
        return entity;
    }

    // Additional store methods...
}
```

---

## 🔧 Troubleshooting

### Common Issues

1. **Circular Dependencies**: Use `Lazy<ICommandPublisher>` in sagas and aggregates to break cycles
2. **Service Registration**: Ensure all aggregates, sagas, and views are properly discovered
3. **Event Handling**: Verify interfaces (`IHandles<T>`, `IHandlesWithEvent<T,U>`, `IProjectOn<T>`) are implemented correctly
4. **Enhanced Return Types**: Remember that `Persist<T>()` methods now return `Task<T>`, providing access to the persisted entity which may have been modified by the store (e.g., with database-generated IDs)

### Debugging Commands

```csharp
// Enable detailed logging
services.AddLogging(configure => configure.AddConsole().SetMinimumLevel(LogLevel.Debug));
```

### Performance Considerations

- Use appropriate service lifetimes (Singleton for read-only, Scoped for persistence)
- Implement proper caching for read models
- Consider event sourcing for audit requirements
- Monitor database performance with OpenTelemetry
- Leverage the enhanced return types to avoid unnecessary database round trips when the entity has been modified by the store

---

## 📖 Documentation

- **Full Documentation**: [GitHub Wiki](https://github.com/CodeShayk/SourceFlow.Net/wiki)
- **API Reference**: [NuGet Package Documentation](https://www.nuget.org/packages/SourceFlow.Net)
- **Release Notes**: [CHANGELOG](../CHANGELOG.md)
- **Architecture Patterns**: [Design Patterns Guide](https://github.com/CodeShayk/SourceFlow.Net/wiki/Architecture-Patterns)

## 🤝 Contributing

We welcome contributions! Please see our [Contributing Guide](../CONTRIBUTING.md) for details.

- 🐛 **Bug Reports** - Create an [issue](https://github.com/CodeShayk/SourceFlow.Net/issues/new/choose)
- 💡 **Feature Requests** - Start a [discussion](https://github.com/CodeShayk/SourceFlow.Net/discussions)
- 📝 **Documentation** - Help improve our [docs](https://github.com/CodeShayk/SourceFlow.Net/wiki)
- 💻 **Code** - Submit [pull requests](https://github.com/CodeShayk/SourceFlow.Net/pulls)

## 🆘 Support

- **Questions**: [GitHub Discussions](https://github.com/CodeShayk/SourceFlow.Net/discussions)
- **Bug Reports**: [GitHub Issues](https://github.com/CodeShayk/SourceFlow.Net/issues/new/choose)
- **Security Issues**: Please report security vulnerabilities responsibly

## 📄 License

This project is licensed under the [MIT License](../LICENSE).

---

<p align="center">
Made with ❤️ by the SourceFlow.Net team to empower developers building event-sourced applications
</p>