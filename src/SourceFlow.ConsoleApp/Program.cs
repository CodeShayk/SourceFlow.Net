// Setup
using SourceFlow.ConsoleApp.Aggregates;
using SourceFlow.ConsoleApp.Events;
using SourceFlow.ConsoleApp.Impl;
using SourceFlow.ConsoleApp.Projections;
using SourceFlow.ConsoleApp.Services;
using SourceFlow.Core.Impl;

var eventStore = new InMemoryEventStore();
var repository = new EventSourcedRepository<BankAccount>(eventStore);
var accountService = new BankAccountService(repository);
var projectionHandler = new AccountSummaryProjectionHandler();

Console.WriteLine("=== Event Sourcing Demo ===\n");

// Create account
var accountId = await accountService.CreateAccountAsync("John Doe", 1000m);
Console.WriteLine($"Created account: {accountId}");

// Perform operations
await accountService.DepositAsync(accountId, 500m);
Console.WriteLine("Deposited $500");

await accountService.WithdrawAsync(accountId, 200m);
Console.WriteLine("Withdrew $200");

await accountService.DepositAsync(accountId, 100m);
Console.WriteLine("Deposited $100");

// Get current state
var account = await accountService.GetAccountAsync(accountId);
Console.WriteLine($"\nCurrent Account State:");
Console.WriteLine($"- ID: {account?.Id}");
Console.WriteLine($"- Holder: {account?.AccountHolderName}");
Console.WriteLine($"- Balance: ${account?.Balance}");
Console.WriteLine($"- Version: {account?.Version}");

// Show event history
var events = await eventStore.GetEventsAsync(accountId);
Console.WriteLine($"\nEvent History ({events.Count()} events):");

foreach (var @event in events)
{
    Console.WriteLine($"- [{@event.Timestamp:HH:mm:ss}] {@event.EventType}");

    // Update projection
    switch (@event)
    {
        case BankAccountCreated created:
            await projectionHandler.HandleAsync(created);
            break;

        case MoneyDeposited deposited:
            await projectionHandler.HandleAsync(deposited);
            break;

        case MoneyWithdrawn withdrawn:
            await projectionHandler.HandleAsync(withdrawn);
            break;

        case AccountClosed closed:
            await projectionHandler.HandleAsync(closed);
            break;
    }
}

// Show projection
var projection = projectionHandler.GetProjection(accountId);
Console.WriteLine($"\nProjection State:");
Console.WriteLine($"- Balance: ${projection?.CurrentBalance}");
Console.WriteLine($"- Transactions: {projection?.TransactionCount}");
Console.WriteLine($"- Last Updated: {projection?.LastUpdated:HH:mm:ss}");
Console.WriteLine($"- Account Closed: {projection?.IsClosed}");

// Close account
await accountService.CloseAccountAsync(accountId, "Customer account close request");
Console.WriteLine($"\nAccount closed");

// Final state
account = await accountService.GetAccountAsync(accountId);
Console.WriteLine($"Final State - Closed: {account?.IsClosed}");

// Show projection
var closed_projection = projectionHandler.GetProjection(accountId);
Console.WriteLine($"\nProjection State:");
Console.WriteLine($"- Balance: ${closed_projection?.CurrentBalance}");
Console.WriteLine($"- Transactions: {closed_projection?.TransactionCount}");
Console.WriteLine($"- Last Updated: {closed_projection?.LastUpdated:HH:mm:ss}");
Console.WriteLine($"- Account Closed: {closed_projection?.IsClosed}");