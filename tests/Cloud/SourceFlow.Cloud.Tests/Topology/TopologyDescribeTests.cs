using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Azure.Messaging.ServiceBus.Administration;
using Google.Cloud.PubSub.V1;
using Json.Schema;
using Shouldly;
using SourceFlow.Cloud.Aws;
using SourceFlow.Cloud.Azure;
using SourceFlow.Cloud.Gcp;
using SourceFlow.Cloud.Tests.Aws;
using SourceFlow.Cloud.Tests.Azure;
using SourceFlow.Cloud.Tests.Gcp;
using SourceFlow.Testing.Conformance;
using SourceFlow.Transport;

namespace SourceFlow.Cloud.Tests.Topology;

/// <summary>UT-TOP (plan 05): naming conventions, the export schema, and the physical descriptions every transport exports (WP-3.5).</summary>
public sealed class TopologyDescribeTests(LocalStackServer localStack, PubSubEmulatorServer pubSub, ServiceBusEmulatorServer serviceBus)
{
    public static TheoryData<string, string> Exports()
    {
        return TopologyFixtureDriftTests.Fixtures();
    }

    // ================================================================ naming

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Id", "UT-TOP-09")]
    [Trait("Covers", "doc 07 3")]
    public void Names_HaveTheEnvironmentPrefix_AndTheProviderSuffixes()
    {
        var topology = new TransportTopology("accounts", ["accounts.events"], ["payments.events"], []) { OrderedEndpoint = true, OrderedTopics = ["accounts.events"] };

        Names(TopologyExports.Describe("aws", topology, "prod-")).ShouldBe(["prod-accounts-dlq.fifo", "prod-accounts.fifo", "prod-accounts-events.fifo", "prod-payments-events.fifo", "prod-payments-events.fifo/prod-accounts.fifo"]);
        Names(TopologyExports.Describe("gcp", topology, "prod-")).ShouldBe(["prod-accounts+dlq", "prod-accounts+dlq", "prod-accounts+in", "prod-accounts", "prod-accounts.events", "prod-payments.events", "prod-accounts~payments.events"]);
        Names(TopologyExports.Describe("azure", topology, "prod-")).ShouldBe(["prod-accounts", "prod-accounts.events", "prod-payments.events", "prod-payments.events/prod-accounts", "prod-payments.events/prod-accounts/$Default"]);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Id", "UT-TOP-09")]
    [InlineData("aws", 80, "^[A-Za-z0-9_.-]+$")]
    [InlineData("gcp", 255, "^[A-Za-z][A-Za-z0-9._~+%-]+$")]
    [InlineData("azure", 260, "^[A-Za-z0-9][A-Za-z0-9._-]*[A-Za-z0-9]$")]
    public void LongAndUnusualNames_FitTheProviderLimits_AndStayDistinct(string transport, int limit, string charset)
    {
        string a = new string('a', 300) + ".orders/eu?";
        string b = new string('a', 300) + ".orders/us?";
        var topology = new TransportTopology(a.Replace('/', '-').Replace('?', '!'), [], [b.Replace('/', '-').Replace('?', '!')], []);

        PhysicalTopology physical = TopologyExports.Describe(transport, topology, "dev-");

        foreach (PhysicalResource resource in physical.Resources.Where(r => r.Kind is PhysicalResourceKind.Queue or PhysicalResourceKind.Topic))
        {
            resource.Name.Length.ShouldBeLessThanOrEqualTo(limit, resource.Name);
            resource.Name.ShouldMatch(charset);
        }

        physical.Resources.Select(r => (r.Kind, r.Name)).Distinct().Count().ShouldBe(physical.Resources.Count, "names stay unique after truncation");
        TopologyExports.Describe(transport, topology, "dev-").Resources.Select(r => r.Name).ShouldBe(physical.Resources.Select(r => r.Name), "deterministic: every service resolves the same names");
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Id", "UT-TOP-09")]
    [InlineData("aws")]
    [InlineData("gcp")]
    [InlineData("azure")]
    public void PublisherAndSubscriber_ResolveTheSameTopic(string transport)
    {
        PhysicalResource Topic(string fixture) => TopologyExports.Describe(transport, TopologyFixtures.Get(fixture), "dev-").Resources.Single(r => r.Kind == PhysicalResourceKind.Topic && r.Logical == "shared");

        PhysicalResource published = Topic("publisher");
        PhysicalResource subscribed = Topic("subscriber");

        subscribed.Name.ShouldBe(published.Name);
        published.Owned.ShouldBeTrue();
        subscribed.Owned.ShouldBeFalse("the publisher owns its topic");
    }

    // ================================================================ schema and ownership

    [Theory]
    [MemberData(nameof(Exports))]
    [Trait("Category", "Unit")]
    [Trait("Id", "UT-TOP-10")]
    [Trait("Covers", "doc 07 5")]
    public void EveryExport_MatchesTheSchema(string transport, string fixture)
    {
        JsonSchema schema = JsonSchema.FromFile(TopologyExports.SchemaPath);
        JsonNode export = JsonNode.Parse(TopologyExports.Export(transport, fixture))!;

        EvaluationResults results = schema.Evaluate(export, new EvaluationOptions { OutputFormat = OutputFormat.List });

        results.IsValid.ShouldBeTrue(string.Join("; ", results.Details.Where(d => d.HasErrors).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))));
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Id", "UT-TOP-10")]
    public void TheSchema_RejectsWhatTheModulesCannotUse()
    {
        JsonSchema schema = JsonSchema.FromFile(TopologyExports.SchemaPath);
        JsonObject export = JsonNode.Parse(TopologyExports.Export("aws", "full"))!.AsObject();

        export["schemaVersion"] = 2;
        schema.Evaluate(export).IsValid.ShouldBeFalse("a newer schema version");
        export["schemaVersion"] = 1;
        export["resources"]![0]!["properties"]!["fifo"] = "yes";
        schema.Evaluate(export).IsValid.ShouldBeFalse("a mistyped provider property");
    }

    [Theory]
    [InlineData("aws")]
    [InlineData("gcp")]
    [InlineData("azure")]
    [Trait("Category", "Unit")]
    [Trait("Id", "UT-TOP-11")]
    [Trait("Covers", "ADR-017")]
    public void OwnedResources_AreTheServicesOwn(string transport)
    {
        TransportTopology topology = TopologyFixtures.Get("ordered");

        PhysicalTopology physical = TopologyExports.Describe(transport, topology, "dev-");

        physical.Resources.Where(r => !r.Owned).Select(r => (r.Logical, r.Role)).ShouldBe([("ordered-in", PhysicalRoles.Topic), ("minimal", PhysicalRoles.RemoteEndpoint)], ignoreOrder: true);
        physical.Resources.Where(r => r.Kind is PhysicalResourceKind.Subscription or PhysicalResourceKind.Rule).ShouldAllBe(r => r.Owned, "a subscription belongs to the subscriber");
        physical.Resources.ShouldContain(r => r.Role == PhysicalRoles.DeadLetter || transport == "azure", "a dead-letter resource (Service Bus has a built-in one)");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Id", "UT-TOP-11")]
    public void ProviderSettings_FollowTheTopologyAndOptions()
    {
        TransportTopology topology = TopologyFixtures.Get("full");

        PhysicalTopology aws = AwsTopology.Describe(new AwsTransportOptions { MessageRetention = TimeSpan.FromDays(2) }, topology);
        PhysicalResource queue = aws.Resources.Single(r => r.Role == PhysicalRoles.Endpoint && r.Kind == PhysicalResourceKind.Queue);
        queue.Properties["maxReceiveCount"]!.GetValue<int>().ShouldBe(7);
        queue.Properties["messageRetentionSeconds"]!.GetValue<int>().ShouldBe(172_800);
        aws.Resources.Single(r => r.Kind == PhysicalResourceKind.Subscription).Properties["filterPolicy"]!.GetValue<string>().ShouldBe(AwsFilterPolicy.Translate("type LIKE 'tck.%'"));

        PhysicalTopology gcp = GcpTopology.Describe(new GcpTransportOptions { ProjectId = "p", MessageRetention = TimeSpan.FromHours(12) }, topology with { MaxDeliveries = 3 });
        PhysicalResource endpoint = gcp.Resources.Single(r => r.Role == PhysicalRoles.Endpoint && r.Kind == PhysicalResourceKind.Subscription);
        endpoint.Properties["maxDeliveryAttempts"]!.GetValue<int>().ShouldBe(5, "Pub/Sub's minimum; the label carries the real limit");
        endpoint.Properties["labels"]![GcpTransport.MaxDeliveriesLabel]!.GetValue<string>().ShouldBe("3");
        endpoint.Properties["messageRetentionDuration"]!.GetValue<string>().ShouldBe("43200s");

        PhysicalTopology azure = AzureTopology.Describe(new AzureTransportOptions { LockDuration = TimeSpan.FromMinutes(1), DuplicateDetectionWindow = TimeSpan.FromMinutes(10) }, topology);
        PhysicalResource azureQueue = azure.Resources.Single(r => r.Kind == PhysicalResourceKind.Queue);
        azureQueue.Properties["lockDuration"]!.GetValue<string>().ShouldBe("PT1M");
        azureQueue.Properties["requiresDuplicateDetection"]!.GetValue<bool>().ShouldBeTrue();
        azureQueue.Properties["duplicateDetectionHistoryTimeWindow"]!.GetValue<string>().ShouldBe("PT10M");
    }

    // ================================================================ the provisioner creates what the export describes

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Cloud", "Aws")]
    [Trait("Requires", "LocalStack")]
    [Trait("Id", "UT-TOP-12")]
    [Trait("Covers", "ADR-017;doc 07 5")]
    public async Task Aws_CreateIfMissing_CreatesEveryDescribedResource()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using AwsTransportFixture f = await AwsTransportFixture.LocalStackAsync(localStack);
        foreach (TopologyFixture fixture in await ProvisionAllAsync(f.Transport, f.Prefix, ct))
        {
            PhysicalTopology physical = f.Aws.Describe(fixture.Topology);
            List<string> topicArns = [.. (await f.Aws.Sns.ListTopicsAsync(new Amazon.SimpleNotificationService.Model.ListTopicsRequest(), ct)).Topics.Select(t => t.TopicArn)];
            foreach (PhysicalResource resource in physical.Resources.Where(r => r.Owned))
            {
                switch (resource.Kind)
                {
                    case PhysicalResourceKind.Queue:
                        (await f.Aws.Sqs.GetQueueUrlAsync(resource.Name, ct)).QueueUrl.ShouldEndWith("/" + resource.Name);
                        break;
                    case PhysicalResourceKind.Topic:
                        topicArns.ShouldContain(a => a.EndsWith(":" + resource.Name, StringComparison.Ordinal), resource.Name);
                        break;
                    case PhysicalResourceKind.Subscription:
                        string topicArn = topicArns.Single(a => a.EndsWith(":" + resource.Properties["topic"]!.GetValue<string>(), StringComparison.Ordinal));
                        List<Amazon.SimpleNotificationService.Model.Subscription> subscriptions = (await f.Aws.Sns.ListSubscriptionsByTopicAsync(topicArn, ct)).Subscriptions;
                        subscriptions.ShouldContain(s => s.Endpoint.EndsWith(":" + resource.Properties["queue"]!.GetValue<string>(), StringComparison.Ordinal), resource.Name);
                        break;
                }
            }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Cloud", "Gcp")]
    [Trait("Requires", "PubSubEmulator")]
    [Trait("Id", "UT-TOP-12")]
    public async Task Gcp_CreateIfMissing_CreatesEveryDescribedResource()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GcpTransportFixture f = await GcpTransportFixture.EmulatorAsync(pubSub);
        foreach (TopologyFixture fixture in await ProvisionAllAsync(f.Transport, f.Prefix, ct))
        {
            foreach (PhysicalResource resource in f.Gcp.Describe(fixture.Topology).Resources.Where(r => r.Owned))
            {
                if (resource.Kind == PhysicalResourceKind.Topic)
                {
                    await f.Gcp.Publisher.GetTopicAsync(new TopicName(PubSubEmulatorServer.ProjectId, resource.Name), ct);
                    continue;
                }

                Google.Cloud.PubSub.V1.Subscription subscription = await f.Gcp.Subscriber.GetSubscriptionAsync(new SubscriptionName(PubSubEmulatorServer.ProjectId, resource.Name), ct);
                subscription.TopicAsTopicName.TopicId.ShouldBe(resource.Properties["topic"]!.GetValue<string>(), resource.Name);
                subscription.EnableMessageOrdering.ShouldBe(resource.Properties["enableMessageOrdering"]!.GetValue<bool>(), resource.Name);
                subscription.Filter.ShouldBe(resource.Properties["filter"]!.GetValue<string>(), resource.Name);
                (subscription.DeadLetterPolicy?.MaxDeliveryAttempts).ShouldBe(resource.Properties["maxDeliveryAttempts"]?.GetValue<int>(), resource.Name);
            }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Cloud", "Azure")]
    [Trait("Requires", "ServiceBusEmulator")]
    [Trait("Id", "UT-TOP-12")]
    public async Task Azure_CreateIfMissing_CreatesEveryDescribedResource()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using AzureTransportFixture f = await AzureTransportFixture.EmulatorAsync(serviceBus);
        var admin = new ServiceBusAdministrationClient(serviceBus.AdministrationConnectionString);
        foreach (TopologyFixture fixture in await ProvisionAllAsync(f.Transport, f.Prefix, ct))
        {
            PhysicalTopology physical = f.Azure.Describe(fixture.Topology);
            foreach (PhysicalResource resource in physical.Resources.Where(r => r.Owned))
            {
                switch (resource.Kind)
                {
                    case PhysicalResourceKind.Queue:
                        QueueProperties queue = await admin.GetQueueAsync(resource.Name, ct);
                        queue.RequiresSession.ShouldBe(resource.Properties["requiresSession"]!.GetValue<bool>(), resource.Name);
                        queue.MaxDeliveryCount.ShouldBe(resource.Properties["maxDeliveryCount"]!.GetValue<int>(), resource.Name);
                        break;
                    case PhysicalResourceKind.Topic:
                        (await admin.TopicExistsAsync(resource.Name, ct)).Value.ShouldBeTrue(resource.Name);
                        break;
                    case PhysicalResourceKind.Subscription:
                        string topic = resource.Properties["topic"]!.GetValue<string>();
                        string name = resource.Properties["subscription"]!.GetValue<string>();
                        SubscriptionProperties subscription = await admin.GetSubscriptionAsync(topic, name, ct);
                        subscription.ForwardTo.ShouldEndWith("/" + resource.Properties["forwardTo"]!.GetValue<string>(), Case.Insensitive);
                        int rules = 0;
                        await foreach (RuleProperties _ in admin.GetRulesAsync(topic, name, ct))
                        {
                            rules++;
                        }

                        rules.ShouldBe(physical.Resources.Count(r => r.Kind == PhysicalResourceKind.Rule && r.Name.StartsWith(resource.Name + "/", StringComparison.Ordinal)), resource.Name);
                        break;
                }
            }
        }
    }

    private static async Task<IReadOnlyList<TopologyFixture>> ProvisionAllAsync(ITransport transport, string prefix, CancellationToken ct)
    {
        IReadOnlyList<TopologyFixture> fixtures = TopologyFixtures.All(prefix + ".");
        foreach (TopologyFixture fixture in fixtures)
        {
            await transport.ProvisionAsync(fixture.Topology, ProvisioningMode.CreateIfMissing, ct);
        }

        return fixtures;
    }

    private static string[] Names(PhysicalTopology physical)
    {
        return [.. physical.Resources.Select(r => r.Name)];
    }
}
