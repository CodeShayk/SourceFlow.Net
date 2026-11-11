using System.Net;
using System.Net.Sockets;
using Amazon.Runtime;
using SourceFlow.Transport;

namespace SourceFlow.Cloud.Aws;

/// <summary>Maps SDK failures to the transport's typed errors (ADR-033 §4), so the core classifies them by transience.</summary>
internal static class AwsErrors
{
    // SQS and SNS have no Retry-After header; the SDK's own backoff starts near one second.
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(1);

    private static readonly HashSet<string> ThrottlingCodes = new(StringComparer.Ordinal)
    {
        "Throttling", "ThrottlingException", "ThrottledException", "RequestThrottled", "RequestThrottledException",
        "TooManyRequestsException", "OverLimit", "RequestLimitExceeded", "SlowDown", "KmsThrottled",
    };

    private static readonly HashSet<string> NotFoundCodes = new(StringComparer.Ordinal)
    {
        "AWS.SimpleQueueService.NonExistentQueue", "QueueDoesNotExist", "NotFound", "NotFoundException", "ResourceNotFoundException",
    };

    /// <summary>True when the error says the queue or topic does not exist.</summary>
    public static bool IsNotFound(Exception error)
    {
        return error is Amazon.SQS.Model.QueueDoesNotExistException or Amazon.SimpleNotificationService.Model.NotFoundException
            || (error is AmazonServiceException service && service.ErrorCode is { } code && NotFoundCodes.Contains(code));
    }

    /// <summary>True when the receipt handle is stale (the lock expired and the message was redelivered, or it is gone).</summary>
    public static bool IsLockLost(Exception error)
    {
        return error is Amazon.SQS.Model.ReceiptHandleIsInvalidException or Amazon.SQS.Model.MessageNotInflightException
            || (error is AmazonServiceException { ErrorCode: "ReceiptHandleIsInvalid" or "MessageNotInflight" or "InvalidParameterValue" } service
                && service.Message.Contains("receipt handle", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Maps an SDK failure; returns null for errors that are not the broker's (cancellation, argument errors).</summary>
    public static TransportException? Map(Exception error, string destination)
    {
        switch (error)
        {
            case TransportException:
            case OperationCanceledException when error.InnerException is not TimeoutException:
                return null;
            case Exception when IsNotFound(error):
                return new DestinationNotFoundException(destination);
            case AmazonServiceException service when IsThrottling(service):
                return new TransportThrottledException($"AWS throttled the call for '{destination}': {service.Message}", service) { RetryAfter = DefaultRetryAfter };
            case AmazonServiceException service when IsUnavailable(service):
                return new TransportUnavailableException($"AWS is unavailable for '{destination}': {service.Message}", service);
            case AmazonServiceException service:
                return new TransportException($"AWS rejected the call for '{destination}' ({service.ErrorCode ?? service.StatusCode.ToString()}): {service.Message}", transient: false, service);
            case HttpRequestException or IOException or SocketException or TimeoutException or OperationCanceledException:
                return new TransportUnavailableException($"AWS is unreachable for '{destination}': {error.Message}", error);
            case AmazonClientException client:
                // Network failures and credential resolution failures surface as client exceptions; both can recover.
                return new TransportUnavailableException($"The AWS client failed for '{destination}': {client.Message}", client);
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

    private static bool IsThrottling(AmazonServiceException error)
    {
        return error.StatusCode == HttpStatusCode.TooManyRequests
            || (error.ErrorCode is { } code && ThrottlingCodes.Contains(code))
            || error is Amazon.SQS.Model.RequestThrottledException or Amazon.SimpleNotificationService.Model.ThrottledException;
    }

    private static bool IsUnavailable(AmazonServiceException error)
    {
        return (int)error.StatusCode >= 500 || error.StatusCode == HttpStatusCode.RequestTimeout || error.InnerException is HttpRequestException or IOException;
    }
}
