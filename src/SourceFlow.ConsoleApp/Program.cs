using Microsoft.Extensions.DependencyInjection;
using SourceFlow;
using SourceFlow.ConsoleApp.Aggregates;
using SourceFlow.ConsoleApp.Sagas;
using SourceFlow.ConsoleApp.Services; // Ensure this using is present

var services = new ServiceCollection();

services.UseSourceFlow();
services.WithSaga<AccountAggregate, AccountSaga>(c =>
{
    return new AccountSaga();
});

services.WithAggregate<AccountAggregate>(c =>
{
    return new AccountAggregate();
});

services.WithService<AccountService>(c => new AccountService());

var serviceProvider = services.BuildServiceProvider();

Console.WriteLine("=== Event Sourcing Demo ===\n");

var accountService = serviceProvider.GetRequiredService<IAccountService>();
var saga = serviceProvider.GetRequiredService<ISagaHandler>();

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
//Console.WriteLine($"- Version: {account?.Version}");

var eventStore = serviceProvider.GetRequiredService<IEventStore>();
// Show event history
var events = await eventStore.LoadAsync(accountId);
Console.WriteLine($"\nEvent History ({events.Count()} events):");

//foreach (var @event in events)
//{
//    Console.WriteLine($"- [{@event.Timestamp:HH:mm:ss}] {@event.EventType}");

//    // Update projection
//    switch (@event)
//    {
//        case AccountCreated created:
//            await projectionHandler.HandleAsync(created);
//            break;

//        case MoneyDeposited deposited:
//            await projectionHandler.HandleAsync(deposited);
//            break;

//        case MoneyWithdrawn withdrawn:
//            await projectionHandler.HandleAsync(withdrawn);
//            break;

//        case AccountClosed closed:
//            await projectionHandler.HandleAsync(closed);
//            break;
//    }
//}

//// Show projection
//var projection = projectionHandler.GetProjection(accountId);
//Console.WriteLine($"\nProjection State:");
//Console.WriteLine($"- Balance: ${projection?.CurrentBalance}");
//Console.WriteLine($"- Transactions: {projection?.TransactionCount}");
//Console.WriteLine($"- Last Updated: {projection?.LastUpdated:HH:mm:ss}");
//Console.WriteLine($"- Account Closed: {projection?.IsClosed}");

// Close account
await accountService.CloseAccountAsync(accountId, "Customer account close request");
Console.WriteLine($"\nAccount closed");

// Final state
account = await accountService.GetAccountAsync(accountId);
Console.WriteLine($"Final State - Closed: {account?.IsClosed}");

//// Show projection
//var closed_projection = projectionHandler.GetProjection(accountId);
//Console.WriteLine($"\nProjection State:");
//Console.WriteLine($"- Balance: ${closed_projection?.CurrentBalance}");
//Console.WriteLine($"- Transactions: {closed_projection?.TransactionCount}");
//Console.WriteLine($"- Last Updated: {closed_projection?.LastUpdated:HH:mm:ss}");
//Console.WriteLine($"- Account Closed: {closed_projection?.IsClosed}");
Console.WriteLine("\nPress any key to exit...");