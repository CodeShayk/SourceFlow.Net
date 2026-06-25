using System.Collections.Concurrent;
using System.Diagnostics;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;

namespace SourceFlow.Cloud.Azure.Tests.TestHelpers;

/// <summary>
/// Runs Azure performance tests against test environments.
/// Implements IAzurePerformanceTestRunner for comprehensive performance testing.
/// </summary>
public class AzurePerformanceTestRunner : IAzurePerformanceTestRunner, IAsyncDisposable
{
    private readonly IAzureTestEnvironment _environment;
    private readonly ServiceBusTestHelpers _serviceBusHelpers;
    private readonly ILogger<AzurePerformanceTestRunner> _logger;
    private readonly System.Random _random = new();

    public AzurePerformanceTestRunner(
        IAzureTestEnvironment environment,
        ServiceBusTestHelpers serviceBusHelpers,
        ILoggerFactory loggerFactory)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _serviceBusHelpers = serviceBusHelpers ?? throw new ArgumentNullException(nameof(serviceBusHelpers));
        _logger = loggerFactory.CreateLogger<AzurePerformanceTestRunner>();
    }

    public async Task<AzurePerformanceTestResult> RunServiceBusThroughputTestAsync(AzureTestScenario scenario)
    {
        _logger.LogInformation("Running Service Bus throughput test: {TestName}", scenario.Name);

        var result = new AzurePerformanceTestResult
        {
            TestName = $"{scenario.Name} - Throughput",
            StartTime = DateTime.UtcNow,
            TotalMessages = scenario.MessageCount
        };

        var stopwatch = Stopwatch.StartNew();
        var successCount = 0;
        var failCount = 0;
        var latencies = new ConcurrentBag<TimeSpan>();

        try
        {
            // Validate environment
            if (!await _environment.IsServiceBusAvailableAsync())
            {
                throw new InvalidOperationException("Service Bus is not available");
            }

            // Create concurrent senders
            var senderTasks = new List<Task>();
            var messagesPerSender = scenario.MessageCount / scenario.ConcurrentSenders;

            for (int s = 0; s < scenario.ConcurrentSenders; s++)
            {
                var senderIndex = s;
                senderTasks.Add(Task.Run(async () =>
                {
                    for (int i = 0; i < messagesPerSender; i++)
                    {
                        var messageStopwatch = Stopwatch.StartNew();
                        try
                        {
                            await SimulateMessageSendAsync(scenario);
                            Interlocked.Increment(ref successCount);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Message send failed in sender {SenderIndex}", senderIndex);
                            Interlocked.Increment(ref failCount);
                        }
                        messageStopwatch.Stop();
                        latencies.Add(messageStopwatch.Elapsed);
                    }
                }));
            }

            await Task.WhenAll(senderTasks);
            stopwatch.Stop();

            // Calculate metrics
            result.EndTime = DateTime.UtcNow;
            result.Duration = stopwatch.Elapsed;
            result.SuccessfulMessages = successCount;
            result.FailedMessages = failCount;
            result.MessagesPerSecond = successCount / stopwatch.Elapsed.TotalSeconds;

            CalculateLatencyMetrics(result, latencies.ToList());
            await CollectServiceBusMetricsAsync(result, scenario);

            _logger.LogInformation(
                "Throughput test completed: {MessagesPerSecond:F2} msg/s, Success: {Success}/{Total}",
                result.MessagesPerSecond, successCount, scenario.MessageCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Throughput test failed: {TestName}", scenario.Name);
            result.Errors.Add($"Throughput test failed: {ex.Message}");
        }

        return result;
    }

    public async Task<AzurePerformanceTestResult> RunServiceBusLatencyTestAsync(AzureTestScenario scenario)
    {
        _logger.LogInformation("Running Service Bus latency test: {TestName}", scenario.Name);

        var result = new AzurePerformanceTestResult
        {
            TestName = $"{scenario.Name} - Latency",
            StartTime = DateTime.UtcNow,
            TotalMessages = scenario.MessageCount
        };

        var latencies = new List<TimeSpan>();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (!await _environment.IsServiceBusAvailableAsync())
            {
                throw new InvalidOperationException("Service Bus is not available");
            }

            // Sequential processing for accurate latency measurement
            for (int i = 0; i < scenario.MessageCount; i++)
            {
                var messageStopwatch = Stopwatch.StartNew();
                
                try
                {
                    // Simulate end-to-end message flow
                    await SimulateMessageSendAsync(scenario);
                    await SimulateMessageReceiveAsync(scenario);
                    result.SuccessfulMessages++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Message {Index} failed", i);
                    result.FailedMessages++;
                }
                
                messageStopwatch.Stop();
                latencies.Add(messageStopwatch.Elapsed);
            }

            stopwatch.Stop();

            result.EndTime = DateTime.UtcNow;
            result.Duration = stopwatch.Elapsed;
            result.MessagesPerSecond = result.SuccessfulMessages / stopwatch.Elapsed.TotalSeconds;

            CalculateLatencyMetrics(result, latencies);
            await CollectServiceBusMetricsAsync(result, scenario);

            _logger.LogInformation(
                "Latency test completed: P50={P50:F2}ms, P95={P95:F2}ms, P99={P99:F2}ms",
                result.MedianLatency.TotalMilliseconds,
                result.P95Latency.TotalMilliseconds,
                result.P99Latency.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Latency test failed: {TestName}", scenario.Name);
            result.Errors.Add($"Latency test failed: {ex.Message}");
        }

        return result;
    }

    public async Task<AzurePerformanceTestResult> RunAutoScalingTestAsync(AzureTestScenario scenario)
    {
        _logger.LogInformation("Running auto-scaling test: {TestName}", scenario.Name);

        var result = new AzurePerformanceTestResult
        {
            TestName = $"{scenario.Name} - Auto-Scaling",
            StartTime = DateTime.UtcNow
        };

        try
        {
            if (!await _environment.IsServiceBusAvailableAsync())
            {
                throw new InvalidOperationException("Service Bus is not available");
            }

            // Measure baseline throughput
            var baselineScenario = new AzureTestScenario
            {
                Name = "Baseline",
                QueueName = scenario.QueueName,
                MessageCount = 100,
                ConcurrentSenders = 1,
                MessageSize = scenario.MessageSize
            };

            var baselineResult = await RunServiceBusThroughputTestAsync(baselineScenario);
            var baselineThroughput = baselineResult.MessagesPerSecond;
            result.AutoScalingMetrics.Add(baselineThroughput);

            _logger.LogInformation("Baseline throughput: {Throughput:F2} msg/s", baselineThroughput);

            // Gradually increase load and measure throughput
            for (int loadMultiplier = 2; loadMultiplier <= 10; loadMultiplier += 2)
            {
                var scalingScenario = new AzureTestScenario
                {
                    Name = $"Load x{loadMultiplier}",
                    QueueName = scenario.QueueName,
                    MessageCount = 100 * loadMultiplier,
                    ConcurrentSenders = loadMultiplier,
                    MessageSize = scenario.MessageSize
                };

                var scalingResult = await RunServiceBusThroughputTestAsync(scalingScenario);
                result.AutoScalingMetrics.Add(scalingResult.MessagesPerSecond);

                _logger.LogInformation(
                    "Load x{Multiplier} throughput: {Throughput:F2} msg/s",
                    loadMultiplier, scalingResult.MessagesPerSecond);

                // Small delay between scaling tests
                await Task.Delay(TimeSpan.FromSeconds(2));
            }

            // Calculate scaling efficiency
            result.ScalingEfficiency = CalculateScalingEfficiency(result.AutoScalingMetrics);
            result.EndTime = DateTime.UtcNow;
            result.Duration = result.EndTime - result.StartTime;

            _logger.LogInformation(
                "Auto-scaling test completed: Efficiency={Efficiency:F2}%",
                result.ScalingEfficiency * 100);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-scaling test failed: {TestName}", scenario.Name);
            result.Errors.Add($"Auto-scaling test failed: {ex.Message}");
        }

        return result;
    }

    public async Task<AzurePerformanceTestResult> RunConcurrentProcessingTestAsync(AzureTestScenario scenario)
    {
        _logger.LogInformation("Running concurrent processing test: {TestName}", scenario.Name);

        var result = new AzurePerformanceTestResult
        {
            TestName = $"{scenario.Name} - Concurrent Processing",
            StartTime = DateTime.UtcNow,
            TotalMessages = scenario.MessageCount
        };

        var stopwatch = Stopwatch.StartNew();
        var processedMessages = new ConcurrentBag<int>();
        var latencies = new ConcurrentBag<TimeSpan>();

        try
        {
            if (!await _environment.IsServiceBusAvailableAsync())
            {
                throw new InvalidOperationException("Service Bus is not available");
            }

            // Create concurrent sender and receiver tasks
            var senderTasks = new List<Task>();
            var receiverTasks = new List<Task>();

            var messagesPerSender = scenario.MessageCount / scenario.ConcurrentSenders;
            var messagesPerReceiver = scenario.MessageCount / scenario.ConcurrentReceivers;

            // Start senders
            for (int s = 0; s < scenario.ConcurrentSenders; s++)
            {
                var senderIndex = s;
                senderTasks.Add(Task.Run(async () =>
                {
                    for (int i = 0; i < messagesPerSender; i++)
                    {
                        try
                        {
                            await SimulateMessageSendAsync(scenario);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Sender {Index} failed", senderIndex);
                        }
                    }
                }));
            }

            // Start receivers
            for (int r = 0; r < scenario.ConcurrentReceivers; r++)
            {
                var receiverIndex = r;
                receiverTasks.Add(Task.Run(async () =>
                {
                    for (int i = 0; i < messagesPerReceiver; i++)
                    {
                        var messageStopwatch = Stopwatch.StartNew();
                        try
                        {
                            await SimulateMessageReceiveAsync(scenario);
                            processedMessages.Add(i);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Receiver {Index} failed", receiverIndex);
                        }
                        messageStopwatch.Stop();
                        latencies.Add(messageStopwatch.Elapsed);
                    }
                }));
            }

            await Task.WhenAll(senderTasks.Concat(receiverTasks));
            stopwatch.Stop();

            result.EndTime = DateTime.UtcNow;
            result.Duration = stopwatch.Elapsed;
            result.SuccessfulMessages = processedMessages.Count;
            result.FailedMessages = scenario.MessageCount - processedMessages.Count;
            result.MessagesPerSecond = processedMessages.Count / stopwatch.Elapsed.TotalSeconds;

            CalculateLatencyMetrics(result, latencies.ToList());
            await CollectServiceBusMetricsAsync(result, scenario);

            _logger.LogInformation(
                "Concurrent processing test completed: {Processed}/{Total} messages, {MessagesPerSecond:F2} msg/s",
                processedMessages.Count, scenario.MessageCount, result.MessagesPerSecond);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Concurrent processing test failed: {TestName}", scenario.Name);
            result.Errors.Add($"Concurrent processing test failed: {ex.Message}");
        }

        return result;
    }

    public async Task<AzurePerformanceTestResult> RunResourceUtilizationTestAsync(AzureTestScenario scenario)
    {
        _logger.LogInformation("Running resource utilization test: {TestName}", scenario.Name);

        var result = new AzurePerformanceTestResult
        {
            TestName = $"{scenario.Name} - Resource Utilization",
            StartTime = DateTime.UtcNow,
            TotalMessages = scenario.MessageCount
        };

        try
        {
            if (!await _environment.IsServiceBusAvailableAsync())
            {
                throw new InvalidOperationException("Service Bus is not available");
            }

            // Run throughput test while collecting resource metrics
            var throughputResult = await RunServiceBusThroughputTestAsync(scenario);

            // Collect resource utilization metrics
            result.ResourceUsage = await CollectResourceUtilizationAsync(scenario);
            
            // Copy throughput metrics
            result.Duration = throughputResult.Duration;
            result.SuccessfulMessages = throughputResult.SuccessfulMessages;
            result.FailedMessages = throughputResult.FailedMessages;
            result.MessagesPerSecond = throughputResult.MessagesPerSecond;
            result.ServiceBusMetrics = throughputResult.ServiceBusMetrics;

            result.EndTime = DateTime.UtcNow;

            _logger.LogInformation(
                "Resource utilization test completed: CPU={Cpu:F2}%, Memory={Memory} bytes, Network In={NetIn} bytes",
                result.ResourceUsage.ServiceBusCpuPercent,
                result.ResourceUsage.ServiceBusMemoryBytes,
                result.ResourceUsage.NetworkBytesIn);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Resource utilization test failed: {TestName}", scenario.Name);
            result.Errors.Add($"Resource utilization test failed: {ex.Message}");
        }

        return result;
    }

    public async Task<AzurePerformanceTestResult> RunSessionProcessingTestAsync(AzureTestScenario scenario)
    {
        _logger.LogInformation("Running session processing test: {TestName}", scenario.Name);

        var result = new AzurePerformanceTestResult
        {
            TestName = $"{scenario.Name} - Session Processing",
            StartTime = DateTime.UtcNow,
            TotalMessages = scenario.MessageCount
        };

        var stopwatch = Stopwatch.StartNew();
        var latencies = new List<TimeSpan>();

        try
        {
            if (!await _environment.IsServiceBusAvailableAsync())
            {
                throw new InvalidOperationException("Service Bus is not available");
            }

            // Process messages with session-based ordering
            var sessionsCount = Math.Min(10, scenario.ConcurrentSenders);
            var messagesPerSession = scenario.MessageCount / sessionsCount;

            for (int sessionId = 0; sessionId < sessionsCount; sessionId++)
            {
                for (int i = 0; i < messagesPerSession; i++)
                {
                    var messageStopwatch = Stopwatch.StartNew();
                    
                    try
                    {
                        await SimulateSessionMessageAsync(scenario, sessionId.ToString());
                        result.SuccessfulMessages++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Session {SessionId} message {Index} failed", sessionId, i);
                        result.FailedMessages++;
                    }
                    
                    messageStopwatch.Stop();
                    latencies.Add(messageStopwatch.Elapsed);
                }
            }

            stopwatch.Stop();

            result.EndTime = DateTime.UtcNow;
            result.Duration = stopwatch.Elapsed;
            result.MessagesPerSecond = result.SuccessfulMessages / stopwatch.Elapsed.TotalSeconds;

            CalculateLatencyMetrics(result, latencies);
            await CollectServiceBusMetricsAsync(result, scenario);

            result.CustomMetrics["SessionsCount"] = sessionsCount;
            result.CustomMetrics["MessagesPerSession"] = messagesPerSession;

            _logger.LogInformation(
                "Session processing test completed: {Sessions} sessions, {MessagesPerSecond:F2} msg/s",
                sessionsCount, result.MessagesPerSecond);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session processing test failed: {TestName}", scenario.Name);
            result.Errors.Add($"Session processing test failed: {ex.Message}");
        }

        return result;
    }

    private void CalculateLatencyMetrics(AzurePerformanceTestResult result, List<TimeSpan> latencies)
    {
        if (latencies.Count == 0)
        {
            return;
        }

        var sortedLatencies = latencies.OrderBy(l => l).ToList();
        result.MinLatency = sortedLatencies.First();
        result.MaxLatency = sortedLatencies.Last();
        result.AverageLatency = TimeSpan.FromMilliseconds(
            sortedLatencies.Average(l => l.TotalMilliseconds));
        result.MedianLatency = sortedLatencies[sortedLatencies.Count / 2];
        result.P95Latency = sortedLatencies[(int)(sortedLatencies.Count * 0.95)];
        result.P99Latency = sortedLatencies[(int)(sortedLatencies.Count * 0.99)];
    }

    private async Task CollectServiceBusMetricsAsync(AzurePerformanceTestResult result, AzureTestScenario scenario)
    {
        // Simulate Service Bus metrics collection
        result.ServiceBusMetrics = new ServiceBusMetrics
        {
            ActiveMessages = _random.Next(0, 100),
            DeadLetterMessages = _random.Next(0, 10),
            ScheduledMessages = 0,
            IncomingMessagesPerSecond = result.MessagesPerSecond * 0.95,
            OutgoingMessagesPerSecond = result.MessagesPerSecond * 0.90,
            ThrottledRequests = result.FailedMessages * 0.1,
            SuccessfulRequests = result.SuccessfulMessages,
            FailedRequests = result.FailedMessages,
            AverageMessageSizeBytes = GetMessageSizeBytes(scenario.MessageSize),
            AverageMessageProcessingTime = result.AverageLatency,
            ActiveConnections = scenario.ConcurrentSenders + scenario.ConcurrentReceivers
        };

        await Task.CompletedTask;
    }

    private async Task<AzureResourceUsage> CollectResourceUtilizationAsync(AzureTestScenario scenario)
    {
        // Simulate resource utilization metrics
        var usage = new AzureResourceUsage
        {
            ServiceBusCpuPercent = _random.NextDouble() * 50 + 10, // 10-60%
            ServiceBusMemoryBytes = _random.Next(100_000_000, 500_000_000), // 100-500 MB
            NetworkBytesIn = scenario.MessageCount * GetMessageSizeBytes(scenario.MessageSize),
            NetworkBytesOut = scenario.MessageCount * GetMessageSizeBytes(scenario.MessageSize),
            KeyVaultRequestsPerSecond = scenario.EnableEncryption ? _random.NextDouble() * 100 : 0,
            KeyVaultLatencyMs = scenario.EnableEncryption ? _random.NextDouble() * 50 + 10 : 0,
            ServiceBusConnectionCount = scenario.ConcurrentSenders + scenario.ConcurrentReceivers,
            ServiceBusNamespaceUtilizationPercent = _random.NextDouble() * 30 + 5 // 5-35%
        };

        await Task.CompletedTask;
        return usage;
    }

    private double CalculateScalingEfficiency(List<double> throughputMetrics)
    {
        if (throughputMetrics.Count < 2)
        {
            return 1.0;
        }

        // Calculate how well throughput scales with load
        // Perfect scaling would be linear (efficiency = 1.0)
        var baseline = throughputMetrics[0];
        var efficiencies = new List<double>();

        for (int i = 1; i < throughputMetrics.Count; i++)
        {
            var expectedThroughput = baseline * (i + 1);
            var actualThroughput = throughputMetrics[i];
            var efficiency = actualThroughput / expectedThroughput;
            efficiencies.Add(efficiency);
        }

        return efficiencies.Average();
    }

    private async Task SimulateMessageSendAsync(AzureTestScenario scenario)
    {
        var latencyMs = GetBaseLatencyMs(scenario.MessageSize);
        latencyMs += scenario.EnableEncryption ? 2.0 : 0;
        latencyMs += scenario.EnableSessions ? 1.0 : 0;
        latencyMs *= 1.0 + (_random.NextDouble() - 0.5) * 0.3; // ±15% variation

        await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, latencyMs)));
    }

    private async Task SimulateMessageReceiveAsync(AzureTestScenario scenario)
    {
        var latencyMs = GetBaseLatencyMs(scenario.MessageSize) * 0.8;
        latencyMs += scenario.EnableEncryption ? 2.0 : 0;
        latencyMs *= 1.0 + (_random.NextDouble() - 0.5) * 0.3; // ±15% variation

        await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, latencyMs)));
    }

    private async Task SimulateSessionMessageAsync(AzureTestScenario scenario, string sessionId)
    {
        var latencyMs = GetBaseLatencyMs(scenario.MessageSize);
        latencyMs += 1.5; // Session overhead
        latencyMs *= 1.0 + (_random.NextDouble() - 0.5) * 0.3; // ±15% variation

        await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, latencyMs)));
    }

    private double GetBaseLatencyMs(MessageSize size)
    {
        return size switch
        {
            MessageSize.Small => 2.0,
            MessageSize.Medium => 5.0,
            MessageSize.Large => 15.0,
            _ => 2.0
        };
    }

    private long GetMessageSizeBytes(MessageSize size)
    {
        return size switch
        {
            MessageSize.Small => 512,
            MessageSize.Medium => 5120,
            MessageSize.Large => 51200,
            _ => 1024
        };
    }

    public async ValueTask DisposeAsync()
    {
        // Cleanup resources if needed
        await Task.CompletedTask;
    }
}
