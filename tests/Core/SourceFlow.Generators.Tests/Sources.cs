namespace SourceFlow.Generators.Tests;

/// <summary>Domain sources compiled by the generator tests.</summary>
internal static class Sources
{
    public const string Usings = """
        using System;
        using System.Collections.Generic;
        using SourceFlow;
        """;

    public const string Account = Usings + """

        namespace Demo;

        [StronglyTypedId] public readonly partial record struct AccountId(Guid Value);

        [MessageType("demo.open", 1)] public sealed record Open(AccountId AccountId, decimal Amount) : ICommand<Account>;
        [MessageType("demo.deposit", 1)] public sealed record Deposit(AccountId AccountId, decimal Amount) : ICommand<Account>;
        [MessageType("demo.transfer", 1)] public sealed record Transfer([property: AggregateId] AccountId From, AccountId To, decimal Amount) : ICommand<Account>;
        [MessageType("demo.opened", 1)] public sealed record Opened(AccountId AccountId, decimal Amount) : IEvent;
        [MessageType("demo.deposited", 1)] public sealed record Deposited(AccountId AccountId, decimal Amount) : IEvent;
        [MessageType("demo.audited", 1)] public sealed record Audited(AccountId AccountId) : IEvent;

        [Aggregate("demo-account", SnapshotEvery = 50)]
        public sealed partial record Account : IAggregate<Account, AccountId>
        {
            public static Account Initial { get; } = new();
            public decimal Balance { get; init; }
            public Decision Decide(Open c, DecisionContext ctx) => Decision.Accept(new Opened(c.AccountId, c.Amount));
            public Decision Decide(Deposit c, DecisionContext ctx) => Decision.Accept(new Deposited(c.AccountId, c.Amount));
            public Decision Decide(Transfer c, DecisionContext ctx) => Decision.NoOp;
            private Account When(Opened e) => this with { Balance = e.Amount };
            private Account When(Deposited e) => this with { Balance = Balance + e.Amount };
        }

        public sealed record Summary(AccountId Id, decimal Balance);

        [Projection("demo-summary", Version = 3)]
        public sealed partial class SummaryProjection : IDocumentProjection<Summary, AccountId>
        {
            public Summary When(Opened e, Summary? current, EventContext ctx) => new(e.AccountId, e.Amount);
            public Summary? When(Deposited e, Summary? current, EventContext ctx) => current is null ? null : current with { Balance = current.Balance + e.Amount };
        }

        [StronglyTypedId] public readonly partial record struct TransferId(Guid Value);
        [MessageType("demo.transfer-started", 1)] public sealed record TransferStarted(TransferId TransferId, AccountId From) : IEvent;
        [MessageType("demo.transfer-timeout", 1)] public sealed record TransferTimedOut(TransferId TransferId) : ITimeout;

        [ProcessManager("demo-transfer")]
        public sealed partial record TransferProcess : IProcessManager<TransferProcess, TransferId>
        {
            public static TransferProcess Initial { get; } = new();
            public int Stage { get; init; }
            public static TransferId CorrelateBy(TransferStarted e) => e.TransferId;
            public Reaction React(TransferStarted e, ReactionContext<TransferId> ctx) => Reaction.Send(new Deposit(e.From, 1)).AndBecome(this with { Stage = 1 });
            public Reaction React(Deposited e, ReactionContext<TransferId> ctx) => Stage == 1 ? Reaction.Complete() : Reaction.Ignore;
            public Reaction React(TransferTimedOut e, ReactionContext<TransferId> ctx) => Reaction.Complete();
        }

        public sealed class AuditHandler : IEventHandler<Audited>
        {
            public System.Threading.Tasks.ValueTask HandleAsync(Audited e, EventContext ctx, System.Threading.CancellationToken ct) => default;
        }

        [SourceFlowModule]
        public sealed partial class DemoModule;
        """;
}
