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

/// <summary>The notifications a customer received.</summary>
[Aggregate("customer-inbox")]
public sealed partial record CustomerInbox : IAggregate<CustomerInbox, CustomerId>
{
    /// <summary>The initial state.</summary>
    public static CustomerInbox Initial { get; } = new();

    /// <summary>True once the welcome message was sent.</summary>
    public bool Welcomed { get; init; }

    /// <summary>Sends the welcome message once.</summary>
    public Decision Decide(SendWelcome c, DecisionContext ctx)
    {
        return Welcomed ? Decision.NoOp : Decision.Accept(new WelcomeSent(c.CustomerId, ctx.Now));
    }

    private CustomerInbox When(WelcomeSent e)
    {
        return this with { Welcomed = true };
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

/// <summary>The Notifications module.</summary>
[SourceFlowModule(typeof(NotificationsJsonContext))]
public sealed partial class NotificationsModule;

/// <summary>JSON metadata for the Notifications module (DC-1).</summary>
[JsonSerializable(typeof(SendWelcome))]
[JsonSerializable(typeof(WelcomeSent))]
[JsonSerializable(typeof(CustomerInbox))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class NotificationsJsonContext : JsonSerializerContext;
