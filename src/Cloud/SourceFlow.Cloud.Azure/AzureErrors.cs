using System.Net.Sockets;
using Azure;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using SourceFlow.Messaging;
using SourceFlow.Transport;

namespace SourceFlow.Cloud.Azure;

/// <summary>Maps Service Bus failures to the transport's typed errors (ADR-033 §4), so the core classifies them by transience.</summary>
internal static class AzureErrors
{
    /// <summary>Service Bus asks clients to back off for 10 seconds when it is busy.</summary>
    public static readonly TimeSpan BusyRetryAfter = TimeSpan.FromSeconds(10);

    /// <summary>True when the queue, topic or subscription does not exist.</summary>
    public static bool IsNotFound(Exception error)
    {
        return error is ServiceBusException { Reason: ServiceBusFailureReason.MessagingEntityNotFound } or RequestFailedException { Status: 404 };
    }

    /// <summary>True when the resource exists already (a concurrent create).</summary>
    public static bool IsAlreadyExists(Exception error)
    {
        return error is ServiceBusException { Reason: ServiceBusFailureReason.MessagingEntityAlreadyExists } or RequestFailedException { Status: 409 };
    }

    /// <summary>True when a message or session lock expired (the message was, or will be, redelivered).</summary>
    public static bool IsLockLost(Exception error)
    {
        return error is ServiceBusException { Reason: ServiceBusFailureReason.MessageLockLost or ServiceBusFailureReason.SessionLockLost }
            || (error is TransportException { InnerException: { } inner } && IsLockLost(inner));
    }

    /// <summary>Maps a failure; returns null for errors that are not the broker's (cancellation, argument errors).</summary>
    public static Exception? Map(Exception error, string destination)
    {
        switch (error)
        {
            case TransportException:
            case OperationCanceledException:
                return null;
            case Exception when IsNotFound(error):
                return new DestinationNotFoundException(destination);
            case ServiceBusException { Reason: ServiceBusFailureReason.MessageSizeExceeded } tooLarge:
                return new TransportException($"Service Bus refused a message for '{destination}' as too large: {tooLarge.Message}", transient: false, tooLarge);
            case ServiceBusException { Reason: ServiceBusFailureReason.ServiceBusy or ServiceBusFailureReason.QuotaExceeded } busy:
                return new TransportThrottledException($"Service Bus throttled the call for '{destination}' ({busy.Reason}): {busy.Message}", busy) { RetryAfter = BusyRetryAfter };
            case ServiceBusException { Reason: ServiceBusFailureReason.ServiceCommunicationProblem or ServiceBusFailureReason.ServiceTimeout } unavailable:
                return new TransportUnavailableException($"Service Bus is unavailable for '{destination}' ({unavailable.Reason}): {unavailable.Message}", unavailable);
            case ServiceBusException other:
                return new TransportException($"Service Bus rejected the call for '{destination}' ({other.Reason}): {other.Message}", other.IsTransient, other);
            case RequestFailedException { Status: 429 or 503 } busy:
                return new TransportThrottledException($"Service Bus throttled the management call for '{destination}' ({busy.Status}): {busy.Message}", busy) { RetryAfter = BusyRetryAfter };
            case RequestFailedException { Status: >= 500 or 408 } unavailable:
                return new TransportUnavailableException($"Service Bus management is unavailable for '{destination}' ({unavailable.Status}): {unavailable.Message}", unavailable);
            case RequestFailedException rejected:
                return new TransportException($"Service Bus management rejected the call for '{destination}' ({rejected.Status} {rejected.ErrorCode}): {rejected.Message}", transient: false, rejected);
            case AuthenticationFailedException or CredentialUnavailableException:
                // A credential that cannot get a token now (an identity endpoint hiccup) can on the next attempt.
                return new TransportUnavailableException($"No token for Service Bus ('{destination}'): {error.Message}", error);
            case UnauthorizedAccessException denied:
                return new TransportException($"Service Bus denied access to '{destination}': {denied.Message}", transient: false, denied);
            case HttpRequestException or IOException or SocketException or TimeoutException:
                return new TransportUnavailableException($"Service Bus is unreachable for '{destination}': {error.Message}", error);
            default:
                return null;
        }
    }

    /// <summary>Runs a call and rethrows a broker failure as a typed transport error.</summary>
    public static async Task<T> CallAsync<T>(string destination, Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Exception e) when (Map(e, destination) is { } mapped)
        {
            throw mapped;
        }
    }

    /// <summary>Runs a call and rethrows a broker failure as a typed transport error.</summary>
    public static async Task CallAsync(string destination, Func<Task> call)
    {
        try
        {
            await call().ConfigureAwait(false);
        }
        catch (Exception e) when (Map(e, destination) is { } mapped)
        {
            throw mapped;
        }
    }

    /// <summary>A message the broker refused for its size.</summary>
    public static MessageTooLargeException TooLarge(MessageEnvelope envelope, long size)
    {
        return new MessageTooLargeException(envelope.CloudEventType, (int)Math.Min(size, int.MaxValue), AzureWire.MaxMessageBytes);
    }
}
