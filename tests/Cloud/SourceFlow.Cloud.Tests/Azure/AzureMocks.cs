using System.Text;
using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using NSubstitute;
using SourceFlow.Cloud.Azure;
using SourceFlow.Messaging;

namespace SourceFlow.Cloud.Tests.Azure;

/// <summary>Substitute Service Bus clients for the transport's unit tests (no broker).</summary>
internal sealed class AzureMocks
{
    private readonly Dictionary<string, ServiceBusSender> _senders = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ServiceBusMessageBatch, List<ServiceBusMessage>> _stores = [];

    public AzureMocks(AzureTransportOptions? options = null, params string[] queues)
    {
        Client.CreateSender(Arg.Any<string>()).Returns(call => Sender(call.Arg<string>()));
        Client.CreateReceiver(Arg.Any<string>(), Arg.Any<ServiceBusReceiverOptions>()).Returns(call =>
        {
            ReceiverOptions.Add((call.Arg<string>(), call.Arg<ServiceBusReceiverOptions>()));
            return Receiver;
        });
        Admin.GetQueueAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            string name = call.Arg<string>();
            return queues.Contains(name, StringComparer.OrdinalIgnoreCase)
                ? Task.FromResult(Response.FromValue(ServiceBusModelFactory.QueueProperties(name, TimeSpan.FromSeconds(30), 1024, false, false, TimeSpan.FromDays(14), TimeSpan.FromDays(1), false, TimeSpan.FromMinutes(10), 10, false, EntityStatus.Active, null, null, string.Empty, false, 256), Substitute.For<Response>()))
                : Task.FromException<Response<QueueProperties>>(new ServiceBusException("Queue was not found", ServiceBusFailureReason.MessagingEntityNotFound));
        });
        Admin.QueueExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            Task.FromResult(Response.FromValue(queues.Contains(call.Arg<string>(), StringComparer.OrdinalIgnoreCase), Substitute.For<Response>())));
        Transport = new AzureTransport(Client, Admin, options ?? new AzureTransportOptions());
    }

    public ServiceBusClient Client { get; } = Substitute.For<ServiceBusClient>();

    public ServiceBusAdministrationClient Admin { get; } = Substitute.For<ServiceBusAdministrationClient>();

    public ServiceBusReceiver Receiver { get; } = Substitute.For<ServiceBusReceiver>();

    public AzureTransport Transport { get; }

    /// <summary>The messages of every batch sent, per entity, in order.</summary>
    public List<(string Entity, List<ServiceBusMessage> Messages)> Batches { get; } = [];

    public List<(string Queue, ServiceBusReceiverOptions Options)> ReceiverOptions { get; } = [];

    /// <summary>How many bytes a batch accepts (each message counts its body plus 512 bytes of properties).</summary>
    public long BatchCapacity { get; set; } = AzureWire.MaxMessageBytes;

    public static MessageEnvelope Envelope(string? key = null, string body = "{\"n\":1}", string type = "orders.placed")
    {
        return new MessageEnvelope
        {
            MessageId = new MessageId(Guid.CreateVersion7()),
            Type = type,
            TypeVersion = 1,
            Source = "orders",
            Time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            PartitionKey = key,
            Body = Encoding.UTF8.GetBytes(body),
        };
    }

    /// <summary>A received message carrying the envelope, as Service Bus returns it.</summary>
    public static ServiceBusReceivedMessage Message(MessageEnvelope envelope, int deliveryCount = 1, string? topic = null, int previous = 0, string? deadLetterReason = null, DateTimeOffset? lockedUntil = null)
    {
        ServiceBusMessage wire = AzureWire.Encode(envelope, topic);
        var properties = new Dictionary<string, object>(wire.ApplicationProperties);
        if (previous > 0)
        {
            properties[AzureWire.DeliveriesProperty] = previous;
        }

        if (deadLetterReason is not null)
        {
            properties["DeadLetterReason"] = deadLetterReason;
        }

        return ServiceBusModelFactory.ServiceBusReceivedMessage(wire.Body, wire.MessageId, sessionId: wire.SessionId, contentType: wire.ContentType, subject: wire.Subject,
            properties: properties, lockTokenGuid: Guid.CreateVersion7(), deliveryCount: deliveryCount, lockedUntil: lockedUntil ?? TimeProvider.System.GetUtcNow().AddSeconds(30), sequenceNumber: 1);
    }

    /// <summary>Makes the next receive return these messages.</summary>
    public void Deliver(params ServiceBusReceivedMessage[] messages)
    {
        Receiver.ReceiveMessagesAsync(Arg.Any<int>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>(messages), Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>([]));
    }

    public ServiceBusSender Sender(string entity)
    {
        if (_senders.TryGetValue(entity, out ServiceBusSender? existing))
        {
            return existing;
        }

        ServiceBusSender sender = Substitute.For<ServiceBusSender>();
        sender.EntityPath.Returns(entity);
        sender.CreateMessageBatchAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var store = new List<ServiceBusMessage>();
            long size = 0;
            ServiceBusMessageBatch batch = ServiceBusModelFactory.ServiceBusMessageBatch(0, store, new CreateMessageBatchOptions { MaxSizeInBytes = BatchCapacity }, message =>
            {
                long next = size + message.Body.ToMemory().Length + 512;
                if (next > BatchCapacity)
                {
                    return false;
                }

                size = next;
                return true;
            });
            _stores.Add(batch, store);
            return ValueTask.FromResult(batch);
        });
        sender.SendMessagesAsync(Arg.Any<ServiceBusMessageBatch>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            Batches.Add((entity, [.. _stores.TryGetValue(call.Arg<ServiceBusMessageBatch>(), out List<ServiceBusMessage>? store) ? store : []]));
            return Task.CompletedTask;
        });
        _senders[entity] = sender;
        return sender;
    }
}
