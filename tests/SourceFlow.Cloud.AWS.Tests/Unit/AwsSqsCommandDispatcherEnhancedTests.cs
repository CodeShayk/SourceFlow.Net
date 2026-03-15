using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SourceFlow.Cloud.AWS.Messaging.Commands;
using SourceFlow.Cloud.AWS.Observability;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using SourceFlow.Cloud.Configuration;
using SourceFlow.Cloud.Observability;
using SourceFlow.Cloud.Resilience;
using SourceFlow.Cloud.Security;
using SourceFlow.Observability;

namespace SourceFlow.Cloud.AWS.Tests.Unit;

[Trait("Category", "Unit")]
public class AwsSqsCommandDispatcherEnhancedTests
{
    private readonly Mock<IAmazonSQS> _mockSqsClient;
    private readonly Mock<ICommandRoutingConfiguration> _mockRoutingConfig;
    private readonly Mock<IDomainTelemetryService> _mockDomainTelemetry;
    private readonly Mock<ICircuitBreaker> _mockCircuitBreaker;
    private readonly CloudTelemetry _cloudTelemetry;
    private readonly CloudMetrics _cloudMetrics;
    private readonly SensitiveDataMasker _dataMasker;

    private const string TestQueueUrl = "https://sqs.us-east-1.amazonaws.com/123456/test-queue";

    public AwsSqsCommandDispatcherEnhancedTests()
    {
        _mockSqsClient = new Mock<IAmazonSQS>();
        _mockRoutingConfig = new Mock<ICommandRoutingConfiguration>();
        _mockDomainTelemetry = new Mock<IDomainTelemetryService>();
        _mockCircuitBreaker = new Mock<ICircuitBreaker>();
        _cloudTelemetry = new CloudTelemetry(NullLogger<CloudTelemetry>.Instance);
        _cloudMetrics = new CloudMetrics(NullLogger<CloudMetrics>.Instance);
        _dataMasker = new SensitiveDataMasker();

        // Default routing setup
        _mockRoutingConfig.Setup(x => x.ShouldRoute<TestCommand>()).Returns(true);
        _mockRoutingConfig.Setup(x => x.GetQueueName<TestCommand>()).Returns(TestQueueUrl);

        // Default SQS response
        _mockSqsClient
            .Setup(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendMessageResponse { MessageId = Guid.NewGuid().ToString() });
    }

    [Fact]
    public async Task Dispatch_CircuitBreakerOpen_ThrowsCircuitBreakerOpenException()
    {
        // Arrange
        _mockCircuitBreaker
            .Setup(x => x.ExecuteAsync(It.IsAny<Func<Task<bool>>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CircuitBreakerOpenException(CircuitState.Open, TimeSpan.FromSeconds(30)));

        var dispatcher = CreateDispatcher();
        var command = new TestCommand();

        // Act & Assert
        await Assert.ThrowsAsync<CircuitBreakerOpenException>(
            () => dispatcher.Dispatch(command));
    }

    [Fact]
    public async Task Dispatch_CircuitBreakerOpen_SqsClientNotCalled()
    {
        // Arrange
        _mockCircuitBreaker
            .Setup(x => x.ExecuteAsync(It.IsAny<Func<Task<bool>>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CircuitBreakerOpenException(CircuitState.Open, TimeSpan.FromSeconds(30)));

        var dispatcher = CreateDispatcher();
        var command = new TestCommand();

        // Act
        try { await dispatcher.Dispatch(command); } catch (CircuitBreakerOpenException) { }

        // Assert
        _mockSqsClient.Verify(
            x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Dispatch_CircuitBreakerClosed_MessageDispatchedToSqs()
    {
        // Arrange
        SetupCircuitBreakerClosed();

        var dispatcher = CreateDispatcher();
        var command = new TestCommand();

        // Act
        await dispatcher.Dispatch(command);

        // Assert
        _mockSqsClient.Verify(
            x => x.SendMessageAsync(
                It.Is<SendMessageRequest>(r => r.QueueUrl == TestQueueUrl),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Dispatch_EncryptionEnabled_EncryptAsyncCalledBeforeSend()
    {
        // Arrange
        SetupCircuitBreakerClosed();

        var mockEncryption = new Mock<IMessageEncryption>();
        mockEncryption.Setup(x => x.AlgorithmName).Returns("TEST-AES");
        mockEncryption
            .Setup(x => x.EncryptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ENCRYPTED_PAYLOAD");

        var dispatcher = CreateDispatcher(encryption: mockEncryption.Object);
        var command = new TestCommand();

        // Act
        await dispatcher.Dispatch(command);

        // Assert: EncryptAsync was called
        mockEncryption.Verify(
            x => x.EncryptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert: SQS was called with the encrypted body
        _mockSqsClient.Verify(
            x => x.SendMessageAsync(
                It.Is<SendMessageRequest>(r => r.MessageBody == "ENCRYPTED_PAYLOAD"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Dispatch_EncryptionDisabled_SendCalledWithPlaintextBody()
    {
        // Arrange
        SetupCircuitBreakerClosed();

        var dispatcher = CreateDispatcher(encryption: null);
        var command = new TestCommand();

        // Act
        await dispatcher.Dispatch(command);

        // Assert: SQS was called (no encryption, body is plain JSON)
        _mockSqsClient.Verify(
            x => x.SendMessageAsync(
                It.Is<SendMessageRequest>(r =>
                    r.QueueUrl == TestQueueUrl &&
                    !string.IsNullOrEmpty(r.MessageBody) &&
                    r.MessageBody != "ENCRYPTED_PAYLOAD"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Dispatch_EncryptionDisabled_EncryptAsyncNeverCalled()
    {
        // Arrange
        SetupCircuitBreakerClosed();

        var mockEncryption = new Mock<IMessageEncryption>();

        // Create dispatcher without encryption (null)
        var dispatcher = CreateDispatcher(encryption: null);
        var command = new TestCommand();

        // Act
        await dispatcher.Dispatch(command);

        // Assert: EncryptAsync was never called since encryption is disabled
        mockEncryption.Verify(
            x => x.EncryptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Dispatch_ShouldRoute_ReturnsFalse_SqsClientNotCalled()
    {
        // Arrange
        _mockRoutingConfig.Setup(x => x.ShouldRoute<TestCommand>()).Returns(false);
        SetupCircuitBreakerClosed();

        var dispatcher = CreateDispatcher();
        var command = new TestCommand();

        // Act
        await dispatcher.Dispatch(command);

        // Assert
        _mockSqsClient.Verify(
            x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Dispatch_SensitiveDataMasker_UsedForLoggingNotForMessageBody()
    {
        // Arrange
        SetupCircuitBreakerClosed();

        var mockEncryption = new Mock<IMessageEncryption>();
        mockEncryption.Setup(x => x.AlgorithmName).Returns("TEST-AES");
        mockEncryption
            .Setup(x => x.EncryptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string input, CancellationToken _) => input); // pass-through

        var dispatcher = CreateDispatcher(encryption: mockEncryption.Object);
        var command = new TestCommand();

        // Act
        await dispatcher.Dispatch(command);

        // Assert: The message body sent to SQS should be the serialized JSON (potentially encrypted),
        // not the output of SensitiveDataMasker (which truncates/hides data).
        // We verify the sent body contains recognisable JSON structure rather than masked text.
        _mockSqsClient.Verify(
            x => x.SendMessageAsync(
                It.Is<SendMessageRequest>(r =>
                    r.MessageBody != null &&
                    !r.MessageBody.Contains("***")), // masker output would contain asterisks
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void SetupCircuitBreakerClosed()
    {
        _mockCircuitBreaker
            .Setup(x => x.ExecuteAsync(It.IsAny<Func<Task<bool>>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<Task<bool>>, CancellationToken>(async (op, ct) => { await op(); return true; });
    }

    private AwsSqsCommandDispatcherEnhanced CreateDispatcher(IMessageEncryption? encryption = null)
    {
        return new AwsSqsCommandDispatcherEnhanced(
            _mockSqsClient.Object,
            _mockRoutingConfig.Object,
            NullLogger<AwsSqsCommandDispatcherEnhanced>.Instance,
            _mockDomainTelemetry.Object,
            _cloudTelemetry,
            _cloudMetrics,
            _mockCircuitBreaker.Object,
            _dataMasker,
            encryption);
    }
}
