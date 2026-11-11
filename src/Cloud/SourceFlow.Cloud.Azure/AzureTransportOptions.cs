using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;

namespace SourceFlow.Cloud.Azure;

/// <summary>
/// Options of the Azure Service Bus transport (doc 07 §6). Every option maps to a client or entity setting; there are no
/// options the transport ignores (GAP-03). Production connects with <see cref="FullyQualifiedNamespace"/> and a
/// <see cref="TokenCredential"/> (managed identity through <see cref="DefaultAzureCredential"/> by default); a connection
/// string is for Development and the emulator, and outside Development the host logs a warning.
/// </summary>
public sealed class AzureTransportOptions
{
    /// <summary>The namespace, for example <c>contoso.servicebus.windows.net</c>. Exclusive with <see cref="ConnectionString"/>.</summary>
    public string? FullyQualifiedNamespace { get; set; }

    /// <summary>The credential for <see cref="FullyQualifiedNamespace"/>. Null uses <see cref="DefaultAzureCredential"/>.</summary>
    public TokenCredential? Credential { get; set; }

    /// <summary>A connection string (Development and the emulator: <c>UseDevelopmentEmulator=true</c>). Exclusive with <see cref="FullyQualifiedNamespace"/>.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// The connection string of the administration (management) endpoint, when it differs from <see cref="ConnectionString"/>:
    /// the emulator serves management on its HTTP port (<c>Endpoint=sb://localhost:5300;…</c>). Null uses <see cref="ConnectionString"/>.
    /// </summary>
    public string? AdministrationConnectionString { get; set; }

    /// <summary>AMQP over TCP (port 5671) or over WebSockets (port 443, through proxies) (<c>ServiceBusClientOptions.TransportType</c>).</summary>
    public ServiceBusTransportType TransportType { get; set; } = ServiceBusTransportType.AmqpTcp;

    /// <summary>How many times the client retries a failed operation before the core's own retry policy takes over (<c>ServiceBusRetryOptions.MaxRetries</c>).</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>The time one attempt of an operation may take (<c>ServiceBusRetryOptions.TryTimeout</c>); also how long accepting a session waits.</summary>
    public TimeSpan TryTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// A prefix for every queue, topic and subscription name, usually the environment (<c>prod-</c>), so that environments
    /// can share a namespace (doc 07 §3). Logical names stay unprefixed in routing.
    /// </summary>
    public string NamePrefix { get; set; } = string.Empty;

    /// <summary>
    /// The lock of new queues (<c>LockDuration</c>, 5 seconds to 5 minutes): how long a message stays locked after a
    /// process dies holding it. A receiver asking for a longer lock keeps the broker lock renewed.
    /// </summary>
    public TimeSpan LockDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long messages wait on new queues and topics before they expire into the dead-letter queue (<c>DefaultMessageTimeToLive</c>).</summary>
    public TimeSpan MessageTimeToLive { get; set; } = TimeSpan.FromDays(14);

    /// <summary>
    /// Broker duplicate detection on new queues (<c>RequiresDuplicateDetection</c> with this window, 20 seconds to 7 days),
    /// complementary to the inbox. Off by default: within the window the broker also drops a dead-letter replay, which
    /// keeps its message id.
    /// </summary>
    public TimeSpan? DuplicateDetectionWindow { get; set; }

    /// <summary>How many sessions (partition keys) one receiver of an ordered endpoint works on at once.</summary>
    public int MaxConcurrentSessions { get; set; } = 8;

    /// <summary>Validates the options; the host fails at startup when one cannot be honoured.</summary>
    /// <exception cref="ArgumentException">An option is missing, conflicting or out of range.</exception>
    public void Validate()
    {
        bool hasNamespace = !string.IsNullOrWhiteSpace(FullyQualifiedNamespace);
        bool hasConnectionString = !string.IsNullOrWhiteSpace(ConnectionString);
        if (hasNamespace == hasConnectionString)
        {
            throw new ArgumentException("Set exactly one of AzureTransportOptions.FullyQualifiedNamespace (with a TokenCredential) and ConnectionString (Development).", nameof(FullyQualifiedNamespace));
        }

        if (hasNamespace && (FullyQualifiedNamespace!.Contains("://", StringComparison.Ordinal) || FullyQualifiedNamespace.Contains('/', StringComparison.Ordinal)))
        {
            throw new ArgumentException("AzureTransportOptions.FullyQualifiedNamespace is a host name (contoso.servicebus.windows.net), not a URI.", nameof(FullyQualifiedNamespace));
        }

        if (hasNamespace && AdministrationConnectionString is not null)
        {
            throw new ArgumentException("AzureTransportOptions.AdministrationConnectionString applies with ConnectionString only.", nameof(AdministrationConnectionString));
        }

        if (hasConnectionString)
        {
            // Throws FormatException for a malformed connection string.
            _ = ServiceBusConnectionStringProperties.Parse(ConnectionString);
            if (AdministrationConnectionString is not null)
            {
                _ = ServiceBusConnectionStringProperties.Parse(AdministrationConnectionString);
            }
        }

        ValidateSettings();
    }

    /// <summary>Validates every option but the connection (for a transport over existing clients).</summary>
    /// <exception cref="ArgumentException">An option is out of range.</exception>
    internal void ValidateSettings()
    {
        if (!Enum.IsDefined(TransportType))
        {
            throw new ArgumentException($"AzureTransportOptions.TransportType '{TransportType}' is not defined.", nameof(TransportType));
        }

        if (MaxRetries is < 0 or > 10)
        {
            throw new ArgumentException("AzureTransportOptions.MaxRetries must be between 0 and 10.", nameof(MaxRetries));
        }

        if (TryTimeout < TimeSpan.FromSeconds(1) || TryTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentException("AzureTransportOptions.TryTimeout must be between 1 second and 10 minutes.", nameof(TryTimeout));
        }

        if (LockDuration < TimeSpan.FromSeconds(5) || LockDuration > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentException("AzureTransportOptions.LockDuration must be between 5 seconds and 5 minutes (the Service Bus limits).", nameof(LockDuration));
        }

        if (MessageTimeToLive < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentException("AzureTransportOptions.MessageTimeToLive must be at least 1 second.", nameof(MessageTimeToLive));
        }

        if (DuplicateDetectionWindow is { } window && (window < TimeSpan.FromSeconds(20) || window > TimeSpan.FromDays(7)))
        {
            throw new ArgumentException("AzureTransportOptions.DuplicateDetectionWindow must be between 20 seconds and 7 days (the Service Bus limits).", nameof(DuplicateDetectionWindow));
        }

        if (MaxConcurrentSessions is < 1 or > 100)
        {
            throw new ArgumentException("AzureTransportOptions.MaxConcurrentSessions must be between 1 and 100.", nameof(MaxConcurrentSessions));
        }

        ArgumentNullException.ThrowIfNull(NamePrefix, nameof(NamePrefix));
        if (NamePrefix.Length > 0 && AzureNaming.Sanitize(NamePrefix) != NamePrefix)
        {
            throw new ArgumentException("AzureTransportOptions.NamePrefix may contain only letters, digits, '-', '_' and '.'.", nameof(NamePrefix));
        }

        if (NamePrefix.Length > 40)
        {
            throw new ArgumentException("AzureTransportOptions.NamePrefix must be at most 40 characters.", nameof(NamePrefix));
        }
    }

    /// <summary>The client options these options describe (transport type and retries).</summary>
    public ServiceBusClientOptions ToClientOptions()
    {
        return new ServiceBusClientOptions
        {
            TransportType = TransportType,
            RetryOptions = new ServiceBusRetryOptions
            {
                Mode = ServiceBusRetryMode.Exponential,
                MaxRetries = MaxRetries,
                TryTimeout = TryTimeout,
            },
        };
    }

    /// <summary>The messaging client these options describe.</summary>
    public ServiceBusClient CreateClient()
    {
        return ConnectionString is { Length: > 0 }
            ? new ServiceBusClient(ConnectionString, ToClientOptions())
            : new ServiceBusClient(FullyQualifiedNamespace, Credential ?? new DefaultAzureCredential(), ToClientOptions());
    }

    /// <summary>The administration client these options describe (provisioning and validation).</summary>
    public ServiceBusAdministrationClient CreateAdministrationClient()
    {
        var options = new ServiceBusAdministrationClientOptions();
        options.Retry.MaxRetries = MaxRetries;
        options.Retry.NetworkTimeout = TryTimeout;
        return ConnectionString is { Length: > 0 }
            ? new ServiceBusAdministrationClient(AdministrationConnectionString ?? ConnectionString, options)
            : new ServiceBusAdministrationClient(FullyQualifiedNamespace, Credential ?? new DefaultAzureCredential(), options);
    }
}
