using System.Text.Json.Nodes;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using Amazon.SQS.Model;
using Bank.Accounts;
using Bank.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SourceFlow.Cloud.Aws;
using SourceFlow.Cloud.Tests.Aws;
using SourceFlow.Hosting;
using SourceFlow.Migration;
using SourceFlow.Query;
using SourceFlow.Runtime;
using SourceFlow.Storage;
using SourceFlow.Storage.PostgreSQL;
using SourceFlow.Storage.Tests.PostgreSql;

namespace SourceFlow.EndToEnd.Tests;

/// <summary>
/// MIG-10..12 (plan 06 §6, WP-5.4): the v2 wire bridge on LocalStack. A v2 SourceFlow.Cloud.AWS producer sends commands
/// to the v3 Accounts service's queue in the v2 shape (a <c>CommandType</c> attribute and a camelCase
/// <c>Command&lt;TPayload&gt;</c> body). With <c>AcceptV2Messages</c>, a mapped type is decided with a deterministic id and
/// an unmapped one is parked; without it, v2 messages are neither handled nor parked.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "EndToEnd")]
[Trait("Cloud", "Aws")]
[Trait("Requires", "LocalStack")]
public sealed class V2BridgeScenarios(PostgreSqlServer postgreSql, LocalStackServer localStack) : IAsyncLifetime
{
    private const string CreateAccount = "SourceFlow.Core.Tests.E2E.Commands.CreateAccount, SourceFlow.Core.Tests, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
    private const string ActivateAccount = "SourceFlow.Core.Tests.E2E.Commands.ActivateAccount, SourceFlow.Core.Tests, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";

    private readonly string _prefix = "v2b" + Guid.CreateVersion7().ToString("N")[^8..] + "-";
    private readonly List<IHost> _hosts = [];
    private AmazonSQSClient _sqs = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await postgreSql.EnsureStartedAsync();
        await localStack.EnsureStartedAsync();
        _sqs = new AmazonSQSClient(new BasicAWSCredentials("test", "test"), new AmazonSQSConfig { ServiceURL = localStack.Endpoint.ToString(), AuthenticationRegion = LocalStackServer.Region });
    }

    public async ValueTask DisposeAsync()
    {
        foreach (IHost host in _hosts)
        {
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
        }

        try
        {
            foreach (string url in (await _sqs.ListQueuesAsync(new ListQueuesRequest { QueueNamePrefix = _prefix })).QueueUrls ?? [])
            {
                await _sqs.DeleteQueueAsync(url);
            }
        }
        catch (AmazonServiceException)
        {
            // Best effort: LocalStack is discarded with the run.
        }

        _sqs.Dispose();
    }

    [Fact]
    [Trait("Id", "MIG-10")]
    [Trait("Id", "MIG-11")]
    [Trait("Covers", "doc 07 8;doc 12 4")]
    public async Task MappedV2Command_IsDecidedWithADeterministicId_AndAnUnmappedOneIsParked()
    {
        IHost accounts = await StartAccountsAsync("accounts", bridge: true);
        string queue = await QueueUrlAsync("accounts");

        // MIG-10: v2 CreateAccount for entity 7 becomes bank.account.open for the mapped v3 id.
        string sent = await SendV2Async(queue, CreateAccount, 7, """{"metadata":{"sequenceNo":1},"name":"CreateAccount","payload":{"id":7,"initialAmount":100,"accountName":"Ada"},"entity":{"id":7,"isNew":false}}""");
        AccountId id = V2Ids.FromLegacy<AccountId>(7);
        IAggregateReader reader = accounts.Services.GetRequiredService<IAggregateReader>();
        await EventuallyAsync(async () => (await reader.LoadAsync<BankAccount, AccountId>(id, cancellationToken: Ct)).Version == new StreamVersion(1), "the v2 command to be decided");
        (await reader.LoadAsync<BankAccount, AccountId>(id, cancellationToken: Ct)).State.Balance.ShouldBe(100m);

        SourceFlowRuntime runtime = accounts.Services.GetRequiredService<SourceFlowRuntime>();
        StoredEvent opened = (await runtime.Storage.Events.ReadStreamAsync(runtime.Options.DefaultTenant, StreamId.For(BankAccount.AggregateName, id), new StreamVersion(1), 1, Ct)).Events.Single();
        opened.Metadata.CausationId.ShouldBe(V2MessageMap.MessageIdOf(sent));   // the command's id: UUIDv5 of the v2 message id
        opened.Metadata.Source.ShouldBe("accounts");

        // MIG-11: a v2 type that isn't on the allow-list is parked, with its v2 name and body.
        await SendV2Async(queue, ActivateAccount, 7, """{"name":"ActivateAccount","payload":{"id":7},"entity":{"id":7}}""");
        IDeadLetterManager deadLetters = accounts.Services.GetRequiredService<IDeadLetterManager>();
        await EventuallyAsync(async () => (await deadLetters.ListAsync(new DeadLetterQuery(), Ct)).Count == 1, "the unmapped v2 command to be parked");
        DeadLetter parked = (await deadLetters.ListAsync(new DeadLetterQuery(), Ct)).Single();
        parked.Reason.ShouldBe("UnknownMessageType");
        parked.Envelope.Type.ShouldBe("SourceFlow.Core.Tests.E2E.Commands.ActivateAccount, SourceFlow.Core.Tests");
        parked.Envelope.Headers[V2MessageMap.UnmappedHeader].ShouldBe("true");
        parked.Envelope.Headers[V2MessageMap.V2TypeHeader].ShouldBe(ActivateAccount);
    }

    [Fact]
    [Trait("Id", "MIG-12")]
    [Trait("Covers", "doc 07 8")]
    public async Task WithoutAcceptV2Messages_V2MessagesAreNeitherHandledNorParked()
    {
        IHost accounts = await StartAccountsAsync("accounts", bridge: false);
        string queue = await QueueUrlAsync("accounts");
        await SendV2Async(queue, CreateAccount, 8, """{"name":"CreateAccount","payload":{"id":8,"initialAmount":5,"accountName":"Bob"},"entity":{"id":8}}""");

        // A v3 command sent after it is handled, so the receiver has seen the v2 message by then.
        ICommandSender commands = accounts.Services.GetRequiredService<ICommandSender>();
        var probe = AccountId.New();
        (await commands.SendAsync(new OpenAccount(probe, "Probe", 1m), null, Ct)).Status.ShouldBe(CommandStatus.Accepted);
        await Task.Delay(TimeSpan.FromSeconds(3), TimeProvider.System, Ct);

        IAggregateReader reader = accounts.Services.GetRequiredService<IAggregateReader>();
        (await reader.LoadAsync<BankAccount, AccountId>(V2Ids.FromLegacy<AccountId>(8), cancellationToken: Ct)).Version.ShouldBe(StreamVersion.None);
        (await accounts.Services.GetRequiredService<IDeadLetterManager>().ListAsync(new DeadLetterQuery(), Ct)).ShouldBeEmpty();
        GetQueueAttributesResponse attributes = await _sqs.GetQueueAttributesAsync(queue, ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"], Ct);
        (attributes.ApproximateNumberOfMessages + attributes.ApproximateNumberOfMessagesNotVisible).ShouldBe(1);   // left for the redrive policy
    }

    private async Task<IHost> StartAccountsAsync(string service, bool bridge)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development, DisableDefaults = true });
        builder.Services.AddSourceFlow(sf =>
        {
            sf.ServiceName = service;
            sf.AddModule<ContractsModule>();
            sf.AddModule<AccountsModule>();
            sf.UsePostgreSql(o =>
            {
                o.DataSource = postgreSql.DataSource;
                o.Schema = PostgreSqlServer.NewSchema("v2bridge");
            });
            var options = new AwsTransportOptions { NamePrefix = _prefix, ReceiveWaitTime = TimeSpan.FromSeconds(1) };
            if (bridge)
            {
                // The allow-list: v2 CreateAccount → bank.account.open, its payload converted to the v3 record.
                options.AcceptV2Messages(map => map.Type("SourceFlow.Core.Tests.E2E.Commands.CreateAccount, SourceFlow.Core.Tests", "bank.account.open", 1, v2 => new JsonObject
                {
                    ["accountId"] = V2Ids.FromLegacy(v2.EntityId ?? v2.Payload!["id"]!.GetValue<int>()).ToString(),
                    ["holder"] = v2.Payload!["accountName"]!.GetValue<string>(),
                    ["initialDeposit"] = v2.Payload!["initialAmount"]!.GetValue<decimal>(),
                }));
            }

            sf.UseTransport(_ => new AwsTransport(
                new AmazonSQSClient(new BasicAWSCredentials("test", "test"), new AmazonSQSConfig { ServiceURL = localStack.Endpoint.ToString(), AuthenticationRegion = LocalStackServer.Region }),
                new AmazonSimpleNotificationServiceClient(new BasicAWSCredentials("test", "test"), new AmazonSimpleNotificationServiceConfig { ServiceURL = localStack.Endpoint.ToString(), AuthenticationRegion = LocalStackServer.Region }),
                options));
            sf.Configure(o => o.PollInterval = TimeSpan.FromMilliseconds(50));
        });

        IHost host = builder.Build();
        _hosts.Add(host);
        await host.StartAsync(Ct);
        return host;
    }

    private async Task<string> QueueUrlAsync(string endpoint)
    {
        foreach (bool ordered in new[] { false, true })
        {
            try
            {
                return (await _sqs.GetQueueUrlAsync(AwsNaming.Queue(_prefix, endpoint, ordered), Ct)).QueueUrl;
            }
            catch (QueueDoesNotExistException)
            {
            }
        }

        throw new InvalidOperationException($"The '{endpoint}' queue was not provisioned.");
    }

    // A v2 AwsSqsCommandDispatcher send: CommandType (assembly-qualified), EntityId and SequenceNo as String attributes.
    private async Task<string> SendV2Async(string queue, string commandType, int entityId, string body)
    {
        var request = new SendMessageRequest
        {
            QueueUrl = queue,
            MessageBody = body,
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["CommandType"] = new() { DataType = "String", StringValue = commandType },
                ["EntityId"] = new() { DataType = "String", StringValue = entityId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                ["SequenceNo"] = new() { DataType = "String", StringValue = "1" },
            },
        };
        if (queue.EndsWith(".fifo", StringComparison.Ordinal))
        {
            request.MessageGroupId = entityId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            request.MessageDeduplicationId = Guid.CreateVersion7().ToString("N");
        }

        return (await _sqs.SendMessageAsync(request, Ct)).MessageId;
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, string what)
    {
        DateTimeOffset deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(60);
        while (!await condition())
        {
            if (TimeProvider.System.GetUtcNow() > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), TimeProvider.System, Ct);
        }
    }
}
