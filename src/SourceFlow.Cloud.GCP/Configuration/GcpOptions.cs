using System;

namespace SourceFlow.Cloud.GCP.Configuration;

/// <summary>
/// Configuration options for the SourceFlow Google Cloud Pub/Sub provider.
/// </summary>
public class GcpOptions
{
    /// <summary>
    /// Google Cloud project id that owns the Pub/Sub topics and subscriptions.
    /// Required. When using the Pub/Sub emulator any non-empty value works.
    /// </summary>
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>Enable command dispatching to Pub/Sub topics.</summary>
    public bool EnableCommandRouting { get; set; } = true;

    /// <summary>Enable event publishing to Pub/Sub topics.</summary>
    public bool EnableEventRouting { get; set; } = true;

    /// <summary>Enable the background command-subscription pull listener.</summary>
    public bool EnableCommandListener { get; set; } = true;

    /// <summary>Enable the background event-subscription pull listener.</summary>
    public bool EnableEventListener { get; set; } = true;

    /// <summary>Maximum number of messages to request per pull.</summary>
    public int MaxMessagesPerPull { get; set; } = 10;

    /// <summary>Ack deadline (seconds) applied to subscriptions created at bootstrap.</summary>
    public int AckDeadlineSeconds { get; set; } = 60;

    /// <summary>Delay applied between pulls that return no messages, to avoid a tight loop.</summary>
    public TimeSpan EmptyPullDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Maximum retry attempts for transient listener failures.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Base delay for exponential backoff on listener failures.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Suffix appended to a queue/topic name to derive its pull subscription id
    /// (e.g. queue <c>orders</c> → subscription <c>orders-sub</c>).
    /// </summary>
    public string SubscriptionSuffix { get; set; } = "-sub";
}
