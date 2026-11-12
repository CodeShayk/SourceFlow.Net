using System.Text.Json.Serialization;
using Bank.Contracts;
using SourceFlow;

namespace Bank.Notifications;

/// <summary>The customer id in the Notifications service (the account id from the integration event).</summary>
[StronglyTypedId]
public readonly partial record struct CustomerId(Guid Value);

/// <summary>Sends the welcome message to a new customer (idempotent).</summary>
[MessageType("notifications.welcome.send", 1)]
public sealed record SendWelcome(CustomerId CustomerId, DateTimeOffset OpenedAt) : ICommand<CustomerInbox>;

/// <summary>The welcome message was sent.</summary>
[MessageType("notifications.welcome.sent", 1)]
public sealed record WelcomeSent(CustomerId CustomerId, DateTimeOffset At) : IEvent;

/// <summary>
/// Sends the customer a statement for a period. Notifications owns this command; other services (Accounts) route it here
/// (ADR-007), so it is a public contract of this service.
/// </summary>
[MessageType("notifications.statement.request", 1)]
public sealed record RequestStatement(CustomerId CustomerId, string Period) : ICommand<CustomerInbox>;

/// <summary>A statement was sent.</summary>
[MessageType("notifications.statement.sent", 1)]
public sealed record StatementSent(CustomerId CustomerId, string Period, DateTimeOffset At) : IEvent;

/// <summary>The notifications a customer received.</summary>
[Aggregate("customer-inbox")]
public sealed partial record CustomerInbox : IAggregate<CustomerInbox, CustomerId>
{
    /// <summary>The initial state.</summary>
    public static CustomerInbox Initial { get; } = new();

    /// <summary>True once the welcome message was sent.</summary>
    public bool Welcomed { get; init; }

    /// <summary>How many statements were sent.</summary>
    public int StatementsSent { get; init; }

    /// <summary>Sends the welcome message once.</summary>
    public Decision Decide(SendWelcome c, DecisionContext ctx)
    {
        return Welcomed ? Decision.NoOp : Decision.Accept(new WelcomeSent(c.CustomerId, ctx.Now));
    }

    /// <summary>Sends a statement every time one is requested: a repeated request is a new statement, a redelivered one is not (the inbox).</summary>
    public Decision Decide(RequestStatement c, DecisionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(c);
        return Decision.Accept(new StatementSent(c.CustomerId, c.Period, ctx.Now));
    }

    private CustomerInbox When(WelcomeSent e)
    {
        return this with { Welcomed = true };
    }

    private CustomerInbox When(StatementSent e)
    {
        return this with { StatementsSent = StatementsSent + 1 };
    }
}

/// <summary>Reacts to the Accounts integration event (received through the service's own subscription, RT-R4).</summary>
public sealed class AccountOpenedHandler(ICommandSender commands) : IEventHandler<AccountOpenedV1>
{
    /// <inheritdoc />
    public async ValueTask HandleAsync(AccountOpenedV1 @event, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        await commands.SendAsync(
            new SendWelcome(new CustomerId(@event.AccountId), @event.OpenedAt),
            new SendOptions { IdempotencyKey = "welcome:" + context.EventId },
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The Notifications commands other services send (<see cref="RequestStatement"/>), without the aggregate that decides
/// them: a sender registers this module and routes the command to the <c>notifications</c> endpoint.
/// </summary>
public sealed class NotificationsCommandsModule : ISourceFlowModule
{
    /// <inheritdoc />
    public static string ModuleName => nameof(NotificationsCommandsModule);

    /// <inheritdoc />
    public static void Register(ISourceFlowModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddMessage<RequestStatement>("notifications.statement.request", 1);
        builder.AddJsonTypeInfoResolver(NotificationsJsonContext.Default);
        builder.AddJsonConverter(new CustomerId.SourceFlowJsonConverter());   // ids travel as strings, as the owner reads them
    }
}

/// <summary>The Notifications module.</summary>
[SourceFlowModule(typeof(NotificationsJsonContext))]
public sealed partial class NotificationsModule;

/// <summary>JSON metadata for the Notifications module (DC-1).</summary>
[JsonSerializable(typeof(SendWelcome))]
[JsonSerializable(typeof(WelcomeSent))]
[JsonSerializable(typeof(RequestStatement))]
[JsonSerializable(typeof(StatementSent))]
[JsonSerializable(typeof(CustomerInbox))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class NotificationsJsonContext : JsonSerializerContext;
