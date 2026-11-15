using System.Text.Json.Serialization;
using Bank.Accounts;
using SourceFlow;

namespace Bank.Transfers;

/// <summary>The transfer id.</summary>
[StronglyTypedId]
public readonly partial record struct TransferId(Guid Value);

/// <summary>Requests a transfer between two accounts.</summary>
[MessageType("bank.transfer.request", 1)]
public sealed record RequestTransfer(TransferId TransferId, AccountId From, AccountId To, decimal Amount) : ICommand<Transfer>;

/// <summary>Marks a transfer completed (sent by the process manager).</summary>
[MessageType("bank.transfer.complete", 1)]
public sealed record CompleteTransfer(TransferId TransferId) : ICommand<Transfer>;

/// <summary>Marks a transfer failed (sent by the process manager).</summary>
[MessageType("bank.transfer.fail", 1)]
public sealed record FailTransfer(TransferId TransferId, string Reason) : ICommand<Transfer>;

/// <summary>A transfer was requested.</summary>
[MessageType("bank.transfer.requested", 1)]
public sealed record TransferRequested(TransferId TransferId, AccountId From, AccountId To, decimal Amount) : IEvent;

/// <summary>A transfer completed.</summary>
[MessageType("bank.transfer.completed", 1)]
public sealed record TransferCompleted(TransferId TransferId) : IEvent;

/// <summary>A transfer failed.</summary>
[MessageType("bank.transfer.failed", 1)]
public sealed record TransferFailed(TransferId TransferId, string Reason) : IEvent;

/// <summary>The transfer did not finish in time.</summary>
[MessageType("bank.transfer.timed-out", 1)]
public sealed record TransferTimedOut(TransferId TransferId) : ITimeout;

/// <summary>The lifecycle of a transfer.</summary>
public enum TransferStatus
{
    /// <summary>Not requested.</summary>
    None = 0,

    /// <summary>In progress.</summary>
    Requested = 1,

    /// <summary>Completed.</summary>
    Completed = 2,

    /// <summary>Failed.</summary>
    Failed = 3,
}

/// <summary>The transfer aggregate: records the request and the outcome.</summary>
[Aggregate("bank-transfer")]
public sealed partial record Transfer : IAggregate<Transfer, TransferId>
{
    /// <summary>The initial state.</summary>
    public static Transfer Initial { get; } = new();

    /// <summary>The status.</summary>
    public TransferStatus Status { get; init; }

    /// <summary>Requests the transfer.</summary>
    public Decision Decide(RequestTransfer c, DecisionContext ctx)
    {
        return this switch
        {
            { Status: not TransferStatus.None } => Decision.NoOp,
            _ when c.Amount <= 0 => Decision.Reject(new Error("transfer.amount_must_be_positive", "Amount must be positive.")),
            _ when c.From == c.To => Decision.Reject(new Error("transfer.same_account", "Cannot transfer to the same account.")),
            _ => Decision.Accept(new TransferRequested(c.TransferId, c.From, c.To, c.Amount)),
        };
    }

    /// <summary>Completes the transfer.</summary>
    public Decision Decide(CompleteTransfer c, DecisionContext ctx)
    {
        return Status == TransferStatus.Requested ? Decision.Accept(new TransferCompleted(c.TransferId)) : Decision.NoOp;
    }

    /// <summary>Fails the transfer.</summary>
    public Decision Decide(FailTransfer c, DecisionContext ctx)
    {
        return Status == TransferStatus.Requested ? Decision.Accept(new TransferFailed(c.TransferId, c.Reason)) : Decision.NoOp;
    }

    private Transfer When(TransferRequested e)
    {
        return this with { Status = TransferStatus.Requested };
    }

    private Transfer When(TransferCompleted e)
    {
        return this with { Status = TransferStatus.Completed };
    }

    private Transfer When(TransferFailed e)
    {
        return this with { Status = TransferStatus.Failed };
    }
}

/// <summary>The stage of a money transfer.</summary>
public enum TransferStage
{
    /// <summary>Not started.</summary>
    None = 0,

    /// <summary>Waiting for the withdrawal.</summary>
    Withdrawing = 1,

    /// <summary>Waiting for the deposit.</summary>
    Depositing = 2,
}

/// <summary>Coordinates a transfer across two accounts with a timeout and compensation (doc 03 §6.1).</summary>
[ProcessManager("money-transfer")]
public sealed partial record MoneyTransfer : IProcessManager<MoneyTransfer, TransferId>
{
    /// <summary>How long a transfer may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    /// <summary>The initial state.</summary>
    public static MoneyTransfer Initial { get; } = new();

    /// <summary>The stage.</summary>
    public TransferStage Stage { get; init; }

    /// <summary>The source account.</summary>
    public AccountId From { get; init; }

    /// <summary>The target account.</summary>
    public AccountId To { get; init; }

    /// <summary>The amount.</summary>
    public decimal Amount { get; init; }

    /// <summary>Starting events correlate by the transfer id.</summary>
    public static TransferId CorrelateBy(TransferRequested e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e.TransferId;
    }

    /// <summary>Starts: withdraw from the source and arm the timeout.</summary>
    public Reaction React(TransferRequested e, ReactionContext<TransferId> ctx)
    {
        ArgumentNullException.ThrowIfNull(e);
        return Reaction.Send(new Withdraw(e.From, e.Amount))
            .Schedule(new TransferTimedOut(e.TransferId), ctx.Now + Timeout)
            .AndBecome(this with { Stage = TransferStage.Withdrawing, From = e.From, To = e.To, Amount = e.Amount });
    }

    /// <summary>The withdrawal happened: deposit to the target.</summary>
    public Reaction React(MoneyWithdrawn e, ReactionContext<TransferId> ctx)
    {
        return Stage == TransferStage.Withdrawing
            ? Reaction.Send(new Deposit(To, Amount)).AndBecome(this with { Stage = TransferStage.Depositing })
            : Reaction.Ignore;
    }

    /// <summary>The deposit happened: complete.</summary>
    public Reaction React(MoneyDeposited e, ReactionContext<TransferId> ctx)
    {
        return Stage == TransferStage.Depositing
            ? Reaction.Send(new CompleteTransfer(ctx.Id)).AndComplete()
            : Reaction.Ignore;
    }

    /// <summary>The source account rejected the withdrawal (for example insufficient funds): fail now (ADR-018).</summary>
    public Reaction React(CommandRejected<Withdraw> r, ReactionContext<TransferId> ctx)
    {
        ArgumentNullException.ThrowIfNull(r);
        return Stage == TransferStage.Withdrawing
            ? Reaction.Send(new FailTransfer(ctx.Id, r.Error.Code)).AndComplete()
            : Reaction.Ignore;
    }

    /// <summary>The timeout fired: fail, compensating a withdrawal that already happened.</summary>
    public Reaction React(TransferTimedOut e, ReactionContext<TransferId> ctx)
    {
        return Stage switch
        {
            TransferStage.Withdrawing => Reaction.Send(new FailTransfer(ctx.Id, "timeout")).AndComplete(),
            TransferStage.Depositing => Reaction.Send(new Deposit(From, Amount)).AndSend(new FailTransfer(ctx.Id, "timeout")).AndComplete(),
            _ => Reaction.Ignore,
        };
    }
}

/// <summary>The Transfers module.</summary>
[SourceFlowModule(typeof(TransfersJsonContext))]
public sealed partial class TransfersModule;

/// <summary>JSON metadata for the Transfers module (DC-1).</summary>
[JsonSerializable(typeof(RequestTransfer))]
[JsonSerializable(typeof(CompleteTransfer))]
[JsonSerializable(typeof(FailTransfer))]
[JsonSerializable(typeof(TransferRequested))]
[JsonSerializable(typeof(TransferCompleted))]
[JsonSerializable(typeof(TransferFailed))]
[JsonSerializable(typeof(TransferTimedOut))]
[JsonSerializable(typeof(MoneyTransfer))]
[JsonSerializable(typeof(CommandRejected<Withdraw>))]
[JsonSerializable(typeof(Transfer))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class TransfersJsonContext : JsonSerializerContext;
