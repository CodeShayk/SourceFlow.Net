using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SourceFlow;
using SourceFlow.Aggregate;
using SourceFlow.ConsoleApp.Aggregates;
using SourceFlow.ConsoleApp.Projections;
using SourceFlow.Saga; // Ensure this using is present

var services = new ServiceCollection();

// Register logging with console provider
services.AddLogging(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});

services.UseSourceFlow(typeof(AccountAggregate).Assembly);

var serviceProvider = services.BuildServiceProvider();

Console.WriteLine("=== Command Sourcing Demo ===\n");

var aggregate = (AccountAggregate)serviceProvider.GetRequiredService<IAccountAggregate>();
var saga = serviceProvider.GetRequiredService<ISaga>();
var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
var viewProvider = serviceProvider.GetRequiredService<IViewProvider>();

// Create account
var accountId = 999;
await aggregate.CreateAccount(accountId, "John Doe", 1000m);
logger.LogInformation("Action=Program_Create_Account, Account: {accountId}", accountId);

// Perform operations
var amount = 500m;
logger.LogInformation("Action=Program_Deposit, Amount={Amount}", amount);
await aggregate.Deposit(accountId, amount);

amount = 200m;
logger.LogInformation("Action=Program_Withdraw, Amount={Amount}", amount);
await aggregate.Withdraw(accountId, amount);

amount = 100m;
logger.LogInformation("Action=Program_Deposit, Amount={Amount}", amount);
await aggregate.Deposit(accountId, amount);

// Find current state
var account = await viewProvider.Find<AccountViewModel>(accountId);
Console.WriteLine($"\nCurrent Account State:");
Console.WriteLine($"- Account EntityId: {account.Id}");
Console.WriteLine($"- Holder: {account.AccountName}");
Console.WriteLine($"- Created On: {account.CreatedDate}");
Console.WriteLine($"- Activated On: {account.ActiveOn}");
Console.WriteLine($"- Current Balance: ${account.CurrentBalance}");
Console.WriteLine($"- Transaction Count: {account.TransactionCount}");
Console.WriteLine($"- Is A/C Closed: {account.IsClosed}");
Console.WriteLine($"- Last updated: {account.LastUpdated}");
Console.WriteLine($"- Version: {account.Version}");

// Show event history
Console.WriteLine($"\nReplay Account History:");
await aggregate.ReplayCommands(accountId);

// Show account summary by replaying history.
account = await viewProvider.Find<AccountViewModel>(accountId);
Console.WriteLine($"\nCurrent Account State:");
Console.WriteLine($"- Account EntityId: {account.Id}");
Console.WriteLine($"- Holder: {account.AccountName}");
Console.WriteLine($"- Created On: {account.CreatedDate}");
Console.WriteLine($"- Activated On: {account.ActiveOn}");
Console.WriteLine($"- Current Balance: ${account.CurrentBalance}");
Console.WriteLine($"- Transaction Count: {account.TransactionCount}");
Console.WriteLine($"- Is A/C Closed: {account.IsClosed}");
Console.WriteLine($"- Last updated: {account.LastUpdated}");
Console.WriteLine($"- Version: {account.Version}");

// Close account
await aggregate.CloseAccount(accountId, "Customer account close request");
Console.WriteLine($"\nClose Account");

//// Final state
account = await viewProvider.Find<AccountViewModel>(accountId);
Console.WriteLine($"\nCurrent Account State:");
Console.WriteLine($"- Account EntityId: {account.Id}");
Console.WriteLine($"- Holder: {account.AccountName}");
Console.WriteLine($"- Created On: {account.CreatedDate}");
Console.WriteLine($"- Activated On: {account.ActiveOn}");
Console.WriteLine($"- Current Balance: ${account.CurrentBalance}");
Console.WriteLine($"- Transaction Count: {account.TransactionCount}");
Console.WriteLine($"- Is A/C Closed: {account.IsClosed}");
Console.WriteLine($"- Last updated: {account.LastUpdated}");
Console.WriteLine($"- Version: {account.Version}");

Console.WriteLine("\nPress any key to exit...");