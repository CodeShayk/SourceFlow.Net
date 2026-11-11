using Bank.Accounts;
using SourceFlow.Runtime;

namespace SourceFlow.Core.Tests.Runtime;

/// <summary>UT-RT (plan 05 §3.3): routing rules RT-R1..R6.</summary>
[Trait("Category", "Unit")]
public sealed class RoutingTests
{
    [Fact]
    [Trait("Id", "UT-RT-01")]
    [Trait("Covers", "RT-R1;ADR-007")]
    public void Command_OwnedAndRouted_IsRejected()
    {
        var routing = new RoutingBuilder();
        routing.Command<Deposit>().To("payments");

        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => routing.Build("accounts", BankModel.Instance));
        error.Message.ShouldContain("RT-R1");
    }

    [Fact]
    [Trait("Id", "UT-RT-02")]
    public void Routes_AreValidatedAgainstTheModel()
    {
        var routing = new RoutingBuilder();
        routing.Publish<AccountOpened>().AsIs().To("accounts.events").OrderedBy(e => e.AccountId);
        routing.Subscribe("payments.events");

        RoutingTable table = routing.Build("accounts", BankModel.Instance);

        table.Endpoint.ShouldBe("accounts");
        PublicationRoute route = table.Publications[typeof(AccountOpened)].ShouldHaveSingleItem();
        route.Destination.ShouldBe("accounts.events");
        route.PartitionKey!(new AccountOpened(BankModel.Account, "x", 1, DateTimeOffset.UnixEpoch)).ShouldBe(BankModel.Account.ToString());
        table.Subscriptions.ShouldHaveSingleItem().Topic.ShouldBe("payments.events");
        table.PublishedTopics.ShouldBe(["accounts.events"]);
    }

    [Theory]
    [Trait("Id", "UT-RT-03")]
    [Trait("Covers", "RT-R5")]
    [InlineData("https://sqs.eu-west-1.amazonaws.com/123/accounts")]
    [InlineData("arn:aws:sns:eu-west-1:123:events")]
    [InlineData("projects/p/topics/t")]
    public void PhysicalNames_AreRejected(string name)
    {
        Should.Throw<ArgumentException>(() => new RoutingBuilder().Subscribe(name)).Message.ShouldContain("RT-R5");
    }

    [Fact]
    [Trait("Id", "UT-RT-04")]
    public void Endpoint_OverridesTheServiceName()
    {
        new RoutingBuilder().Endpoint("accounts-v2").Build("accounts", BankModel.Instance).Endpoint.ShouldBe("accounts-v2");
    }

    [Fact]
    [Trait("Id", "UT-RT-05")]
    public void ReplyOnReject_RequiresTo()
    {
        Should.Throw<InvalidOperationException>(() => new RoutingBuilder().Command<Deposit>().ReplyOnReject());
    }

    [Fact]
    [Trait("Id", "UT-RT-06")]
    public void Model_ExposesOwnersAggregatesAndProjections()
    {
        RuntimeModel model = BankModel.Instance;

        model.CommandOwners[typeof(Deposit)].Name.ShouldBe("bank-account");
        model.Aggregates[typeof(BankAccount)].DecidedCommands.Count.ShouldBe(4);
        model.Projections.ShouldHaveSingleItem().Collection.ShouldBe("account-summary@1");
    }

    [Fact]
    [Trait("Id", "UT-RT-07")]
    public void Publication_ToAnUnregisteredContract_IsRejected()
    {
        var routing = new RoutingBuilder();
        routing.Publish<AccountOpened>().As(e => new Unregistered(e.AccountId.Value)).To("accounts.events");

        Should.Throw<SourceFlow.Messaging.UnknownMessageTypeException>(() => routing.Build("accounts", BankModel.Instance));
    }

#pragma warning disable SF0001 // deliberately unregistered
    private sealed record Unregistered(Guid Id) : IIntegrationEvent;
#pragma warning restore SF0001
}
