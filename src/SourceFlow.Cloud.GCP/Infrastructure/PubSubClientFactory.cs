using Google.Api.Gax;
using Google.Cloud.PubSub.V1;

namespace SourceFlow.Cloud.GCP.Infrastructure;

/// <summary>
/// Creates Pub/Sub API clients. <see cref="EmulatorDetection.EmulatorOrProduction"/> makes the
/// clients honour the <c>PUBSUB_EMULATOR_HOST</c> environment variable when set (local
/// development / CI) and fall back to Application Default Credentials otherwise.
/// </summary>
public static class PubSubClientFactory
{
    public static PublisherServiceApiClient CreatePublisher()
        => new PublisherServiceApiClientBuilder
        {
            EmulatorDetection = EmulatorDetection.EmulatorOrProduction
        }.Build();

    public static SubscriberServiceApiClient CreateSubscriber()
        => new SubscriberServiceApiClientBuilder
        {
            EmulatorDetection = EmulatorDetection.EmulatorOrProduction
        }.Build();
}
