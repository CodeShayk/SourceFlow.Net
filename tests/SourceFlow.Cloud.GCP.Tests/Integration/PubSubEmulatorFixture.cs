using Google.Cloud.PubSub.V1;
using SourceFlow.Cloud.GCP.Infrastructure;

namespace SourceFlow.Cloud.GCP.Tests.Integration;

/// <summary>
/// Shared fixture for Pub/Sub integration tests. Connects to the Pub/Sub emulator when
/// <c>PUBSUB_EMULATOR_HOST</c> is set; otherwise tests using it are skipped.
/// </summary>
public sealed class PubSubEmulatorFixture
{
    public bool EmulatorAvailable { get; }
    public string ProjectId { get; }
    public PublisherServiceApiClient? Publisher { get; }
    public SubscriberServiceApiClient? Subscriber { get; }

    public PubSubEmulatorFixture()
    {
        EmulatorAvailable = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PUBSUB_EMULATOR_HOST"));
        ProjectId = "sourceflow-it-" + Guid.NewGuid().ToString("N").Substring(0, 8);

        if (EmulatorAvailable)
        {
            Publisher = PubSubClientFactory.CreatePublisher();
            Subscriber = PubSubClientFactory.CreateSubscriber();
        }
    }

    /// <summary>Pulls from a subscription, retrying briefly to absorb publish/pull propagation lag.</summary>
    public async Task<IReadOnlyList<ReceivedMessage>> PullWithRetryAsync(SubscriptionName subscription, int expected, int attempts = 10)
    {
        var collected = new List<ReceivedMessage>();
        for (var i = 0; i < attempts && collected.Count < expected; i++)
        {
            var response = await Subscriber!.PullAsync(new PullRequest
            {
                SubscriptionAsSubscriptionName = subscription,
                MaxMessages = expected
            });

            if (response.ReceivedMessages.Count > 0)
            {
                collected.AddRange(response.ReceivedMessages);
                await Subscriber.AcknowledgeAsync(subscription, response.ReceivedMessages.Select(m => m.AckId));
            }
            else
            {
                await Task.Delay(250);
            }
        }
        return collected;
    }
}
