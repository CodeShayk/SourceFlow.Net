using System.Net.Sockets;
using Google.Api.Gax.Grpc;
using Google.Rpc;
using Grpc.Core;
using SourceFlow.Transport;

namespace SourceFlow.Cloud.Gcp;

/// <summary>Maps gRPC failures to the transport's typed errors (ADR-033 §4), so the core classifies them by transience.</summary>
internal static class GcpErrors
{
    // Without a RetryInfo detail, throttling is retried after about a second (the client libraries' first backoff).
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(1);

    /// <summary>True when the error says the topic or subscription does not exist.</summary>
    public static bool IsNotFound(Exception error)
    {
        return error is RpcException { StatusCode: StatusCode.NotFound };
    }

    /// <summary>True when the error says the resource exists already (a concurrent create).</summary>
    public static bool IsAlreadyExists(Exception error)
    {
        return error is RpcException { StatusCode: StatusCode.AlreadyExists };
    }

    /// <summary>Maps a failure; returns null for errors that are not the broker's (cancellation, argument errors).</summary>
    public static TransportException? Map(Exception error, string destination)
    {
        switch (error)
        {
            case TransportException:
            case OperationCanceledException:
                return null;
            case RpcException { StatusCode: StatusCode.NotFound }:
                return new DestinationNotFoundException(destination);
            case RpcException { StatusCode: StatusCode.ResourceExhausted } throttled:
                return new TransportThrottledException($"Pub/Sub throttled the call for '{destination}': {throttled.Status.Detail}", throttled) { RetryAfter = RetryAfter(throttled) };
            case RpcException { StatusCode: StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.Aborted or StatusCode.Internal or StatusCode.Unknown or StatusCode.Cancelled or StatusCode.Unauthenticated } unavailable:
                // Unauthenticated: a token that expired mid-call; the credential refreshes on the next call.
                return new TransportUnavailableException($"Pub/Sub is unavailable for '{destination}' ({unavailable.StatusCode}): {unavailable.Status.Detail}", unavailable);
            case RpcException rejected:
                return new TransportException($"Pub/Sub rejected the call for '{destination}' ({rejected.StatusCode}): {rejected.Status.Detail}", transient: false, rejected);
            case HttpRequestException or IOException or SocketException or TimeoutException:
                return new TransportUnavailableException($"Pub/Sub is unreachable for '{destination}': {error.Message}", error);
            default:
                return null;
        }
    }

    /// <summary>Runs a call and rethrows a broker failure as a typed transport error; a cancelled call stays a cancellation.</summary>
    public static async Task<T> CallAsync<T>(string destination, Func<Task<T>> call, CancellationToken cancellationToken)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(e.Message, e, cancellationToken);
        }
        catch (Exception e) when (Map(e, destination) is { } mapped)
        {
            throw mapped;
        }
    }

    /// <summary>Runs a call and rethrows a broker failure as a typed transport error; a cancelled call stays a cancellation.</summary>
    public static Task CallAsync(string destination, Func<Task> call, CancellationToken cancellationToken)
    {
        return CallAsync(destination, async () =>
        {
            await call().ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    private static TimeSpan RetryAfter(RpcException error)
    {
        try
        {
            return error.GetStatusDetail<RetryInfo>()?.RetryDelay?.ToTimeSpan() is { } delay && delay > TimeSpan.Zero ? delay : DefaultRetryAfter;
        }
        catch (Exception e) when (e is Google.Protobuf.InvalidProtocolBufferException or FormatException)
        {
            return DefaultRetryAfter;
        }
    }
}
