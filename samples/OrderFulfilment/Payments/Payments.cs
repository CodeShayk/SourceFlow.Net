using System.Text.Json.Serialization;
using SourceFlow;
using SourceFlow.Hosting;

namespace Payments;

/// <summary>An order, as Payments knows it: the payment's id.</summary>
[StronglyTypedId]
public readonly partial record struct OrderId(Guid Value);

/// <summary>The rejection codes of the Payments service.</summary>
public static class PaymentErrors
{
    /// <summary>The card issuer declined the charge. Trying again later may work.</summary>
    public const string Declined = "payments.declined";

    /// <summary>The gateway suspects fraud. Don't try again.</summary>
    public const string FraudSuspected = "payments.fraud_suspected";

    /// <summary>The amount is zero or negative.</summary>
    public const string InvalidAmount = "payments.invalid_amount";
}

// ================================================================ contract: what other services send and receive

/// <summary>Charges an order's amount to the customer's card.</summary>
[MessageType("payments.charge", 1)]
public sealed record Charge(OrderId OrderId, decimal Amount) : ICommand<Payment>;

/// <summary>Refunds a captured payment (compensation).</summary>
[MessageType("payments.refund", 1)]
public sealed record Refund(OrderId OrderId, Guid PaymentId, decimal Amount) : ICommand<Payment>;

/// <summary>The money was captured.</summary>
[MessageType("payments.payment-captured", 1)]
public sealed record PaymentCaptured(Guid PaymentId, OrderId OrderId, decimal Amount) : IEvent;

/// <summary>The payment was refunded. <see cref="Amount"/> is zero when it had been refunded already.</summary>
[MessageType("payments.refunded", 1)]
public sealed record Refunded(Guid PaymentId, OrderId OrderId, decimal Amount) : IEvent;

// ================================================================ the gateway port

/// <summary>What the gateway answered.</summary>
public enum GatewayOutcome
{
    /// <summary>The money was captured.</summary>
    Approved = 0,

    /// <summary>The issuer declined.</summary>
    Declined = 1,

    /// <summary>The gateway's fraud check failed.</summary>
    FraudSuspected = 2,
}

/// <summary>A payment service provider. Every call carries an idempotency key, so a repeated call has no second effect.</summary>
public interface IPaymentGateway
{
    /// <summary>Authorizes and captures <paramref name="amount"/> for <paramref name="order"/>.</summary>
    ValueTask<GatewayOutcome> CaptureAsync(Guid order, decimal amount, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Refunds a captured payment.</summary>
    ValueTask RefundAsync(Guid payment, decimal amount, string idempotencyKey, CancellationToken cancellationToken);
}

/// <summary>
/// The sample's gateway, a sandbox with test amounts: cents of .51 are declined, cents of .66 are suspected fraud, and
/// everything else is approved.
/// </summary>
public sealed class SandboxPaymentGateway : IPaymentGateway
{
    /// <inheritdoc />
    public ValueTask<GatewayOutcome> CaptureAsync(Guid order, decimal amount, string idempotencyKey, CancellationToken cancellationToken)
    {
        decimal cents = (amount - decimal.Truncate(amount)) * 100m;
        return ValueTask.FromResult(cents switch
        {
            51m => GatewayOutcome.Declined,
            66m => GatewayOutcome.FraudSuspected,
            _ => GatewayOutcome.Approved,
        });
    }

    /// <inheritdoc />
    public ValueTask RefundAsync(Guid payment, decimal amount, string idempotencyKey, CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }
}

// ================================================================ the service

/// <summary>An order's payment.</summary>
[Aggregate("payment")]
public sealed partial record Payment : IAggregate<Payment, OrderId>
{
    /// <summary>The state before the first event.</summary>
    public static Payment Initial { get; } = new();

    /// <summary>The captured payment, once there is one.</summary>
    public Guid? PaymentId { get; init; }

    /// <summary>The captured amount.</summary>
    public decimal Captured { get; init; }

    /// <summary>True once the payment was refunded.</summary>
    public bool Refunded { get; init; }

    private Payment When(PaymentCaptured e)
    {
        return this with { PaymentId = e.PaymentId, Captured = e.Amount };
    }

    private Payment When(Refunded e)
    {
        return this with { Refunded = true };
    }
}

/// <summary>Charges through the gateway. The command's id is the idempotency key, so a redelivered command charges once.</summary>
public sealed class ChargeHandler(IPaymentGateway gateway) : ICommandHandler<Charge, Payment>
{
    /// <inheritdoc />
    public async ValueTask<Decision> HandleAsync(Charge command, Payment state, DecisionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(state);
        if (state.PaymentId is not null)
        {
            return Decision.NoOp;   // captured already
        }

        if (command.Amount <= 0)
        {
            return Decision.Reject(new Error(PaymentErrors.InvalidAmount, "The amount must be positive."));
        }

        GatewayOutcome outcome = await gateway.CaptureAsync(command.OrderId.Value, command.Amount, context.CommandId.ToString(), cancellationToken).ConfigureAwait(false);
        return outcome switch
        {
            GatewayOutcome.Approved => Decision.Accept(new PaymentCaptured(context.CommandId.Value, command.OrderId, command.Amount)),
            GatewayOutcome.FraudSuspected => Decision.Reject(new Error(PaymentErrors.FraudSuspected, "The payment gateway suspects fraud.")),
            _ => Decision.Reject(new Error(PaymentErrors.Declined, "The card was declined.")),
        };
    }
}

/// <summary>Refunds through the gateway. A refund always answers, so the compensation that asked for it can finish.</summary>
public sealed class RefundHandler(IPaymentGateway gateway) : ICommandHandler<Refund, Payment>
{
    /// <inheritdoc />
    public async ValueTask<Decision> HandleAsync(Refund command, Payment state, DecisionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(state);
        if (state.Refunded || state.PaymentId != command.PaymentId)
        {
            return Decision.Accept(new Refunded(command.PaymentId, command.OrderId, 0m));   // nothing (more) to refund
        }

        await gateway.RefundAsync(command.PaymentId, state.Captured, context.CommandId.ToString(), cancellationToken).ConfigureAwait(false);
        return Decision.Accept(new Refunded(command.PaymentId, command.OrderId, state.Captured));
    }
}

/// <summary>The Payments service's topic.</summary>
public static class PaymentsTopics
{
    /// <summary>Where Payments publishes <see cref="PaymentCaptured"/> and <see cref="Refunded"/>.</summary>
    public const string Events = "payments.events";
}

/// <summary>The Payments module: the payment aggregate and its handlers.</summary>
[SourceFlowModule(typeof(PaymentsJsonContext))]
public sealed partial class PaymentsModule;

/// <summary>
/// The Payments contract for other services: its commands and events, without the aggregate that decides them. A sender
/// registers this module and routes the commands to the <c>payments</c> endpoint.
/// </summary>
public sealed class PaymentsContractsModule : ISourceFlowModule
{
    /// <inheritdoc />
    public static string ModuleName => nameof(PaymentsContractsModule);

    /// <inheritdoc />
    public static void Register(ISourceFlowModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddMessage<Charge>("payments.charge", 1);
        builder.AddMessage<Refund>("payments.refund", 1);
        builder.AddMessage<PaymentCaptured>("payments.payment-captured", 1);
        builder.AddMessage<Refunded>("payments.refunded", 1);
        builder.AddJsonTypeInfoResolver(PaymentsJsonContext.Default);
        builder.AddJsonConverter(new OrderId.SourceFlowJsonConverter());
    }
}

/// <summary>How the Payments service is set up: what a host, the design-time export and the tests share.</summary>
public static class PaymentsService
{
    /// <summary>The service name and endpoint.</summary>
    public const string Name = "payments";

    /// <summary>
    /// Adds the Payments service: its module, and the publication of its events to <see cref="PaymentsTopics.Events"/>.
    /// The gateway is the <see cref="IPaymentGateway"/> in the container (default: the sandbox).
    /// </summary>
    public static SourceFlowBuilder AddPaymentsService(this SourceFlowBuilder sf)
    {
        ArgumentNullException.ThrowIfNull(sf);
        sf.ServiceName = Name;
        sf.AddModule<PaymentsModule>();
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<IPaymentGateway>(sf.Services, new SandboxPaymentGateway());
        sf.Routing(r =>
        {
            r.Publish<PaymentCaptured>().AsIs().To(PaymentsTopics.Events);
            r.Publish<Refunded>().AsIs().To(PaymentsTopics.Events);
        });
        return sf;
    }
}

/// <summary>JSON metadata for the Payments service (DC-1).</summary>
[JsonSerializable(typeof(Charge))]
[JsonSerializable(typeof(Refund))]
[JsonSerializable(typeof(PaymentCaptured))]
[JsonSerializable(typeof(Refunded))]
[JsonSerializable(typeof(Payment))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class PaymentsJsonContext : JsonSerializerContext;
