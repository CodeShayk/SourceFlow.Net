using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SourceFlow.Cloud.AWS.Messaging.Events;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using SourceFlow.Cloud.Configuration;
using SourceFlow.Cloud.Observability;
using SourceFlow.Cloud.Resilience;
using SourceFlow.Cloud.Security;
using SourceFlow.Observability;

namespace SourceFlow.Cloud.AWS.Tests.Unit;

[Trait("Category", "Unit")]
public class AwsSnsEventDispatcherEnhancedTests
{
    private readonly Mock<IAmazonSimpleNotificationService> _mockSnsClient;
    private readonly Mock<IEventRoutingConfiguration> _mockRoutingConfig;
    private readonly Mock<IDomainTelemetryService> _mockDomainTelemetry;
    private readonly Mock<ICircuitBreaker> _mockCircuitBreaker;
    private readonly CloudTelemetry _cloudTelemetry;
    private readonly CloudMetrics _cloudMetrics;
    private readonly SensitiveDataMasker _dataMasker;

    private const string TestTopicArn = "arn:aws:sns:us-east-1:123456:test-topic";

    public AwsSnsEventDispatcherEnhancedTests()
    {
        _mockSnsClient = new Mock<IAmazonSimpleNotificationService>();
        _mockRoutingConfig = new Mock<IEventRoutingConfiguration>();
        _mockDomainTelemetry = new Mock<IDomainTelemetryService>();
        _mockCircuitBreaker = new Mock<ICircuitBreaker>();
        _cloudTelemetry = new CloudTelemetry(NullLogger<CloudTelemetry>.Instance);
        _cloudMetrics = new CloudMetrics(NullLogger<CloudMetrics>.Instance);
        _dataMasker = new SensitiveDataMasker();

        // Default routing setup
        _mockRoutingConfig.Setup(x => x.ShouldRoute<TestEvent>()).Returns(true);
        _mockRoutingConfig.Setup(x => x.GetTopicName<TestEvent>()).Returns(TestTopicArn);

        // Default SNS response
        _mockSnsClient
            .Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublishResponse { MessageId = Guid.NewGuid().ToString() });
    }

    [Fact]
    public async Task Dispatch_CircuitBreakerOpen_ThrowsCircuitBreakerOpenException()
    {
        // Arrange
        _mockCircuitBreaker
            .Setup(x => x.ExecuteAsync(It.IsAny<Func<Task<bool>>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CircuitBreakerOpenException(CircuitState.Open, TimeSpan.FromSeconds(30)));

        var dispatcher = CreateDispatcher();
        var @event = new TestEvent();

        // Act & Assert
        await Assert.ThrowsAsync<CircuitBreakerOpenException>(
            () => dispatcher.Dispatch(@event));
    }

    [Fact]
    public async Task Dispatch_CircuitBreakerOpen_SnsClientNotCalled()
    {
        // Arrange
        _mockCircuitBreaker
            .Setup(x => x.ExecuteAsync(It.IsAny<Func<Task<bool>>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CircuitBreakerOpenException(CircuitState.Open, TimeSpan.FromSeconds(30)));

        var dispatcher = CreateDispatcher();
        var @event = new TestEvent();

        // Act
        try { await dispatcher.Dispatch(@event); } catch (CircuitBreakerOpenException) { }

        // Assert
        _mockSnsClient.Verify(
            x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Dispatch_CircuitBreakerClosed_EventPublishedToSns()
    {
        // Arrange
        SetupCircuitBreakerClosed();

        var dispatcher = CreateDispatcher();
        var @event = new TestEvent();

        // Act
        await dispatcher.Dispatch(@event);

        // Assert
        _mockSnsClient.Verify(
            x => x.PublishAsync(
                It.Is<PublishRequest>(r => r.TopicArn == TestTopicArn),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Dispatch_EncryptionEnabled_EncryptAsyncCalledBeforePublish()
    {
        // Arrange
        SetupCircuitBreakerClosed();

        var mockEncryption = new Mock<IMessageEncryption>();
        mockEncryption.Setup(x => x.AlgorithmName).Returns("TEST-AES");
        mockEncryption
            .Setup(x => x.EncryptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ENCRYPTED_PAYLOAD");

        var dispatcher = CreateDispatcher(encryption: mockEncryption.Object);
        var @event = new TestEvent();

        // Act
        await dispatcher.Dispatch(@event);

        // Assert: EncryptAsync was called
        mockEncryption.Verify(
            x => x.EncryptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert: SNS was called with the encrypted message body
        _mockSnsClient.Verify(
            x => x.PublishAsync(
                It.Is<PublishRequest>(r => r.Message == "ENCRYPTED_PAYLOAD"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Dispatch_EncryptionDisabled_PublishCalledWithPlaintextBody()
    {
        // Arrange
        SetupCircuitBreakerClosed();

        var dispatcher = CreateDispatcher(encryption: null);
        var @event = new TestEvent();

        // Act
        await dispatcher.Dispatch(@event);

        // Assert: SNS was called with a non-empty, non-encrypted message body
        _mockSnsClient.Verify(
            x => x.PublishAsync(
                It.Is<PublishRequest>(r =>
                    r.TopicArn == TestTopicArn &&
                    !string.IsNullOrEmpty(r.Message) &&
                    r.Message != "ENCRYPTED_PAYLOAD"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Dispatch_ShouldRoute_ReturnsFalse_SnsClientNotCalled()
    {
        // Arrange
        _mockRoutingConfig.Setup(x => x.ShouldRoute<TestEvent>()).Returns(false);
        SetupCircuitBreakerClosed();

        var dispatcher = CreateDispatcher();
        var @event = new TestEvent();

        // Act
        await dispatcher.Dispatch(@event);

        // Assert
        _mockSnsClient.Verify(
            x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void SetupCircuitBreakerClosed()
    {
        _mockCircuitBreaker
            .Setup(x => x.ExecuteAsync(It.IsAny<Func<Task<bool>>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<Task<bool>>, CancellationToken>(async (op, ct) => { await op(); return true; });
    }

    private AwsSnsEventDispatcherEnhanced CreateDispatcher(IMessageEncryption? encryption = null)
    {
        return new AwsSnsEventDispatcherEnhanced(
            _mockSnsClient.Object,
            _mockRoutingConfig.Object,
            NullLogger<AwsSnsEventDispatcherEnhanced>.Instance,
            _mockDomainTelemetry.Object,
            _cloudTelemetry,
            _cloudMetrics,
            _mockCircuitBreaker.Object,
            _dataMasker,
            encryption);
    }
}
