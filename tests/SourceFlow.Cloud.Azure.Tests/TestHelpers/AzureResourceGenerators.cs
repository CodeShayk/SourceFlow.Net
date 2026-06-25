using FsCheck;

namespace SourceFlow.Cloud.Azure.Tests.TestHelpers;

/// <summary>
/// FsCheck generators for Azure test resources.
/// </summary>
public static class AzureResourceGenerators
{
    /// <summary>
    /// Generates arbitrary Azure test resource sets for property-based testing.
    /// </summary>
    public static Arbitrary<AzureTestResourceSet> AzureTestResourceSet()
    {
        var resourceGen = from resourceCount in Gen.Choose(1, 10)
                         from resources in Gen.ListOf(resourceCount, AzureTestResource())
                         select new AzureTestResourceSet
                         {
                             Resources = resources.ToList()
                         };

        return Arb.From(resourceGen);
    }

    /// <summary>
    /// Generates arbitrary Azure test resources.
    /// </summary>
    public static Gen<AzureTestResource> AzureTestResource()
    {
        var resourceTypeGen = Gen.Elements(
            AzureResourceType.ServiceBusQueue,
            AzureResourceType.ServiceBusTopic,
            AzureResourceType.ServiceBusSubscription,
            AzureResourceType.KeyVaultKey,
            AzureResourceType.KeyVaultSecret
        );

        var nameGen = from prefix in Gen.Elements("test", "temp", "ci", "dev")
                     from suffix in Gen.Choose(1000, 9999)
                     select $"{prefix}-{suffix}";

        var resourceGen = from type in resourceTypeGen
                         from name in nameGen
                         from requiresCleanup in Gen.Frequency(
                             Tuple.Create(9, Gen.Constant(true)),  // 90% require cleanup
                             Tuple.Create(1, Gen.Constant(false))) // 10% don't require cleanup
                         select new AzureTestResource
                         {
                             Type = type,
                             Name = name,
                             RequiresCleanup = requiresCleanup,
                             Tags = new Dictionary<string, string>
                             {
                                 ["Environment"] = "Test",
                                 ["CreatedBy"] = "PropertyTest",
                                 ["Timestamp"] = DateTimeOffset.UtcNow.ToString("O")
                             }
                         };

        return resourceGen;
    }

    /// <summary>
    /// Generates Service Bus queue configurations.
    /// </summary>
    public static Gen<ServiceBusQueueConfig> ServiceBusQueueConfig()
    {
        var configGen = from requiresSession in Arb.Generate<bool>()
                       from enableDuplicateDetection in Arb.Generate<bool>()
                       from maxDeliveryCount in Gen.Choose(1, 10)
                       select new ServiceBusQueueConfig
                       {
                           RequiresSession = requiresSession,
                           EnableDuplicateDetection = enableDuplicateDetection,
                           MaxDeliveryCount = maxDeliveryCount
                       };

        return configGen;
    }

    /// <summary>
    /// Generates Service Bus topic configurations.
    /// </summary>
    public static Gen<ServiceBusTopicConfig> ServiceBusTopicConfig()
    {
        var configGen = from enableBatchedOperations in Arb.Generate<bool>()
                       from maxSizeInMegabytes in Gen.Elements(1024, 2048, 3072, 4096, 5120)
                       select new ServiceBusTopicConfig
                       {
                           EnableBatchedOperations = enableBatchedOperations,
                           MaxSizeInMegabytes = maxSizeInMegabytes
                       };

        return configGen;
    }

    /// <summary>
    /// Generates Key Vault key configurations.
    /// </summary>
    public static Gen<KeyVaultKeyConfig> KeyVaultKeyConfig()
    {
        var configGen = from keySize in Gen.Elements(2048, 3072, 4096)
                       from enabled in Arb.Generate<bool>()
                       select new KeyVaultKeyConfig
                       {
                           KeySize = keySize,
                           Enabled = enabled
                       };

        return configGen;
    }

    // Generators for subscription filtering property tests

    public static Gen<FilteredMessageBatch> GenerateFilteredMessageBatch()
    {
        return from highCount in Gen.Choose(1, 5)
               from lowCount in Gen.Choose(1, 5)
               select new FilteredMessageBatch
               {
                   Messages = GenerateMessagesWithPriority(highCount, lowCount),
                   HighPriorityCount = highCount,
                   LowPriorityCount = lowCount
               };
    }

    private static List<global::Azure.Messaging.ServiceBus.ServiceBusMessage> GenerateMessagesWithPriority(int highCount, int lowCount)
    {
        var messages = new List<global::Azure.Messaging.ServiceBus.ServiceBusMessage>();

        for (int i = 0; i < highCount; i++)
        {
            var message = new global::Azure.Messaging.ServiceBus.ServiceBusMessage($"High priority message {i}")
            {
                MessageId = Guid.NewGuid().ToString()
            };
            message.ApplicationProperties["Priority"] = "High";
            messages.Add(message);
        }

        for (int i = 0; i < lowCount; i++)
        {
            var message = new global::Azure.Messaging.ServiceBus.ServiceBusMessage($"Low priority message {i}")
            {
                MessageId = Guid.NewGuid().ToString()
            };
            message.ApplicationProperties["Priority"] = "Low";
            messages.Add(message);
        }

        return messages;
    }

    public static Gen<NumericFilteredMessageBatch> GenerateNumericFilteredMessages()
    {
        return from threshold in Gen.Choose(50, 150)
               from aboveCount in Gen.Choose(2, 5)
               from belowCount in Gen.Choose(2, 5)
               select new NumericFilteredMessageBatch
               {
                   Messages = GenerateMessagesWithNumericValues(threshold, aboveCount, belowCount),
                   Threshold = threshold,
                   ExpectedCount = aboveCount
               };
    }

    private static List<global::Azure.Messaging.ServiceBus.ServiceBusMessage> GenerateMessagesWithNumericValues(
        int threshold, 
        int aboveCount, 
        int belowCount)
    {
        var messages = new List<global::Azure.Messaging.ServiceBus.ServiceBusMessage>();
        var random = new System.Random();

        // Messages above threshold
        for (int i = 0; i < aboveCount; i++)
        {
            var value = threshold + random.Next(1, 100);
            var message = new global::Azure.Messaging.ServiceBus.ServiceBusMessage($"Message with value {value}")
            {
                MessageId = Guid.NewGuid().ToString()
            };
            message.ApplicationProperties["Value"] = value;
            messages.Add(message);
        }

        // Messages below threshold
        for (int i = 0; i < belowCount; i++)
        {
            var value = threshold - random.Next(1, 50);
            var message = new global::Azure.Messaging.ServiceBus.ServiceBusMessage($"Message with value {value}")
            {
                MessageId = Guid.NewGuid().ToString()
            };
            message.ApplicationProperties["Value"] = value;
            messages.Add(message);
        }

        return messages;
    }

    public static Gen<FanOutScenario> GenerateFanOutScenario()
    {
        return from subscriptionCount in Gen.Choose(2, 4)
               from messageCount in Gen.Choose(2, 5)
               select new FanOutScenario
               {
                   SubscriptionNames = Enumerable.Range(1, subscriptionCount)
                       .Select(i => $"sub-{i}")
                       .ToList(),
                   Messages = Enumerable.Range(1, messageCount)
                       .Select(i => new global::Azure.Messaging.ServiceBus.ServiceBusMessage($"Fanout message {i}")
                       {
                           MessageId = Guid.NewGuid().ToString(),
                           Subject = "FanOutTest"
                       })
                       .ToList()
               };
    }
}

/// <summary>
/// Represents a set of Azure test resources.
/// </summary>
public class AzureTestResourceSet
{
    public List<AzureTestResource> Resources { get; set; } = new();
}

/// <summary>
/// Represents an Azure test resource.
/// </summary>
public class AzureTestResource
{
    public AzureResourceType Type { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool RequiresCleanup { get; set; } = true;
    public Dictionary<string, string> Tags { get; set; } = new();
}

/// <summary>
/// Azure resource types for testing.
/// </summary>
public enum AzureResourceType
{
    ServiceBusQueue,
    ServiceBusTopic,
    ServiceBusSubscription,
    KeyVaultKey,
    KeyVaultSecret
}

/// <summary>
/// Service Bus queue configuration for testing.
/// </summary>
public class ServiceBusQueueConfig
{
    public bool RequiresSession { get; set; }
    public bool EnableDuplicateDetection { get; set; }
    public int MaxDeliveryCount { get; set; } = 10;
}

/// <summary>
/// Service Bus topic configuration for testing.
/// </summary>
public class ServiceBusTopicConfig
{
    public bool EnableBatchedOperations { get; set; }
    public int MaxSizeInMegabytes { get; set; } = 1024;
}

/// <summary>
/// Key Vault key configuration for testing.
/// </summary>
public class KeyVaultKeyConfig
{
    public int KeySize { get; set; } = 2048;
    public bool Enabled { get; set; } = true;
}


/// <summary>
/// FsCheck generators for Azure test scenarios.
/// </summary>
public static class AzureTestScenarioGenerators
{
    /// <summary>
    /// Generates arbitrary Azure test scenarios for property-based testing.
    /// </summary>
    public static Arbitrary<AzureTestScenario> AzureTestScenario()
    {
        var scenarioGen = from name in Gen.Elements("CommandRouting", "EventPublishing", "SessionOrdering", "DuplicateDetection")
                         from messageCount in Gen.Choose(10, 100)
                         from enableSessions in Arb.Generate<bool>()
                         from enableDuplicateDetection in Arb.Generate<bool>()
                         from enableEncryption in Arb.Generate<bool>()
                         from queueName in Gen.Elements("test-commands.fifo", "test-notifications")
                         select new AzureTestScenario
                         {
                             Name = $"{name}_{Guid.NewGuid():N}",
                             QueueName = queueName,
                             MessageCount = messageCount,
                             EnableSessions = enableSessions,
                             EnableDuplicateDetection = enableDuplicateDetection,
                             EnableEncryption = enableEncryption
                         };

        return Arb.From(scenarioGen);
    }

    /// <summary>
    /// Generates arbitrary Azure performance test scenarios.
    /// </summary>
    public static Arbitrary<AzureTestScenario> AzurePerformanceTestScenario()
    {
        var scenarioGen = from name in Gen.Elements("ThroughputTest", "LatencyTest", "ConcurrencyTest")
                         from messageCount in Gen.Choose(50, 500)
                         from concurrentSenders in Gen.Choose(1, 5)
                         from messageSize in Gen.Elements(MessageSize.Small, MessageSize.Medium)
                         select new AzureTestScenario
                         {
                             Name = $"{name}_{Guid.NewGuid():N}",
                             QueueName = "test-commands.fifo",
                             MessageCount = messageCount,
                             ConcurrentSenders = concurrentSenders,
                             MessageSize = messageSize
                         };

        return Arb.From(scenarioGen);
    }

    /// <summary>
    /// Generates arbitrary Azure message patterns.
    /// </summary>
    public static Arbitrary<AzureMessagePattern> AzureMessagePattern()
    {
        var patternGen = from patternType in Gen.Elements(
                             MessagePatternType.SimpleCommandQueue,
                             MessagePatternType.EventTopicFanout,
                             MessagePatternType.SessionBasedOrdering,
                             MessagePatternType.DuplicateDetection,
                             MessagePatternType.DeadLetterHandling,
                             MessagePatternType.EncryptedMessages,
                             MessagePatternType.ManagedIdentityAuth,
                             MessagePatternType.RBACPermissions)
                         from messageCount in Gen.Choose(5, 50)
                         select new AzureMessagePattern
                         {
                             PatternType = patternType,
                             MessageCount = messageCount
                         };

        return Arb.From(patternGen);
    }
}

/// <summary>
/// Represents an Azure message pattern for testing.
/// </summary>
public class AzureMessagePattern
{
    public MessagePatternType PatternType { get; set; }
    public int MessageCount { get; set; }
}

/// <summary>
/// Types of message patterns to test.
/// </summary>
public enum MessagePatternType
{
    SimpleCommandQueue,
    EventTopicFanout,
    SessionBasedOrdering,
    DuplicateDetection,
    DeadLetterHandling,
    EncryptedMessages,
    ManagedIdentityAuth,
    RBACPermissions,
    AdvancedKeyVault
}

/// <summary>
/// Result of running an Azure test scenario.
/// </summary>
public class AzureTestScenarioResult
{
    public bool Success { get; set; }
    public int MessagesProcessed { get; set; }
    public bool MessageOrderPreserved { get; set; }
    public int DuplicatesDetected { get; set; }
    public bool EncryptionWorked { get; set; }
    public List<string> Errors { get; set; } = new();
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// Result of testing a message pattern.
/// </summary>
public class AzureMessagePatternResult
{
    public bool Success { get; set; }
    public List<string> Errors { get; set; } = new();
    public Dictionary<string, object> Metrics { get; set; } = new();
}

// Supporting types for property tests
public class FilteredMessageBatch
{
    public List<global::Azure.Messaging.ServiceBus.ServiceBusMessage> Messages { get; set; } = new();
    public int HighPriorityCount { get; set; }
    public int LowPriorityCount { get; set; }
}

public class NumericFilteredMessageBatch
{
    public List<global::Azure.Messaging.ServiceBus.ServiceBusMessage> Messages { get; set; } = new();
    public int Threshold { get; set; }
    public int ExpectedCount { get; set; }
}

public class FanOutScenario
{
    public List<string> SubscriptionNames { get; set; } = new();
    public List<global::Azure.Messaging.ServiceBus.ServiceBusMessage> Messages { get; set; } = new();
}

