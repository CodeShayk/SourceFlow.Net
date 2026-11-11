using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using SourceFlow.Messaging;

namespace SourceFlow.Runtime;

/// <summary>
/// Runtime options. Every option is honoured or rejected by validation at startup (GAP-03, "no inert options").
/// </summary>
public sealed class SourceFlowOptions
{
    /// <summary>The service name: envelope <c>source</c>, default endpoint and inbox consumer (required).</summary>
    [Required]
    [RegularExpression("^[a-z][a-z0-9-]{0,62}$", ErrorMessage = "ServiceName must be lowercase letters, digits and dashes, starting with a letter.")]
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>The tenant used when no tenant resolver supplies one.</summary>
    [Required]
    public string DefaultTenant { get; set; } = MessageEnvelope.DefaultTenant;

    /// <summary>Re-decide attempts after a concurrency conflict (ADR-008).</summary>
    [Range(0, 50)]
    public int MaxConcurrencyRetries { get; set; } = 5;

    /// <summary>The number of outbox partitions (doc 04 §7).</summary>
    [Range(1, 1024)]
    public int OutboxPartitions { get; set; } = 16;

    /// <summary>Rows dispatched per outbox claim.</summary>
    [Range(1, 10_000)]
    public int OutboxBatchSize { get; set; } = 100;

    /// <summary>Dispatch attempts before an outbox row is parked.</summary>
    [Range(1, 1000)]
    public int OutboxMaxAttempts { get; set; } = 10;

    /// <summary>Poll interval when no commit notification arrives.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Lease time-to-live for outbox and subscription partitions (doc 05 §7.4).</summary>
    public TimeSpan LeaseTtl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Concurrent receive workers (RX-R1).</summary>
    [Range(1, 1024)]
    public int ReceiveConcurrency { get; set; } = Math.Max(2, Environment.ProcessorCount * 2);

    /// <summary>Bounded channel size per receive worker (RX-R2).</summary>
    [Range(1, 10_000)]
    public int PrefetchPerWorker { get; set; } = 16;

    /// <summary>Immediate in-process retries of a transient failure before a delayed retry.</summary>
    [Range(0, 10)]
    public int ImmediateRetries { get; set; } = 3;

    /// <summary>Delayed retry schedule after immediate retries (doc 04 §9.2); exhausting it parks the message.</summary>
    public IList<TimeSpan> DelayedRetries { get; } = [TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10), TimeSpan.FromHours(1)];

    /// <summary>Events read per subscription batch.</summary>
    [Range(1, 10_000)]
    public int SubscriptionBatchSize { get; set; } = 256;

    /// <summary>Partitions of an async subscription (a power of two, doc 06 §3).</summary>
    [Range(1, 1024)]
    public int SubscriptionPartitions { get; set; } = 1;

    /// <summary>How long shutdown waits for in-flight work (RX-R6).</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The node id used as lease owner; defaults to a random id per process.</summary>
    [Required]
    public string NodeId { get; set; } = Environment.MachineName.ToLowerInvariant() + "-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + "-" + System.Security.Cryptography.RandomNumberGenerator.GetHexString(8, lowercase: true);
}

/// <summary>Validates the attribute rules of <see cref="SourceFlowOptions"/> (source-generated, AOT-safe).</summary>
[OptionsValidator]
public sealed partial class SourceFlowOptionsValidator : IValidateOptions<SourceFlowOptions>;

/// <summary>Validates the time-based rules of <see cref="SourceFlowOptions"/> that attributes can't express trim-safely.</summary>
public sealed class SourceFlowOptionsTimingValidator : IValidateOptions<SourceFlowOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, SourceFlowOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        Between(options.PollInterval, TimeSpan.FromMilliseconds(10), TimeSpan.FromMinutes(10), nameof(options.PollInterval), failures);
        Between(options.LeaseTtl, TimeSpan.FromSeconds(1), TimeSpan.FromHours(1), nameof(options.LeaseTtl), failures);
        Between(options.ShutdownTimeout, TimeSpan.Zero, TimeSpan.FromMinutes(10), nameof(options.ShutdownTimeout), failures);
        if (options.LeaseTtl <= options.PollInterval)
        {
            failures.Add($"{nameof(options.LeaseTtl)} ({options.LeaseTtl}) must be longer than {nameof(options.PollInterval)} ({options.PollInterval}) so leases are renewed in time.");
        }

        if (options.DelayedRetries.Any(d => d <= TimeSpan.Zero))
        {
            failures.Add($"{nameof(options.DelayedRetries)} must contain positive delays.");
        }

        if ((options.SubscriptionPartitions & (options.SubscriptionPartitions - 1)) != 0)
        {
            failures.Add($"{nameof(options.SubscriptionPartitions)} must be a power of two (doc 06 §3).");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Between(TimeSpan value, TimeSpan min, TimeSpan max, string name, List<string> failures)
    {
        if (value < min || value > max)
        {
            failures.Add($"{name} must be between {min} and {max}; got {value}.");
        }
    }
}
